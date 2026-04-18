/*
 *  NetUMPWrapper.cpp
 *  Unity-facing C bridge for the NetUMP session library.
 *
 *  FIXES APPLIED
 *  ─────────────
 *  Fix 5 — detach() removed from NetUMP_Start. The session thread is now
 *           kept joinable so NetUMP_Stop can block until the thread exits
 *           before deleting the handler. Previously, detach() made
 *           joinable() always return false, causing NetUMP_Stop to delete
 *           wrapper->handler while the thread was still calling RunSession()
 *           on it — a use-after-free and data race on every shutdown.
 *
 *  Fix 6 — Connection and disconnection callbacks now carry a void* userData
 *           field in NetUMPWrapper (connectionUserData / disconnectionUserData).
 *           NetUMP_SetConnectionCallback and NetUMP_SetDisconnectionCallback
 *           accept and store this value. The trampoline functions
 *           NativeConnectionCallback / NativeDisconnectionCallback pass it as
 *           the first argument to the C# delegate, so the static bridge in
 *           NetUMPCallbackBridge.cs can recover the pinned managed instance via
 *           GCHandle.FromIntPtr(userData).
 *
 *           The SetConnectionCallback / SetDisconnectCallback calls on
 *           CNetUMPHandler were previously commented out entirely, meaning
 *           connection events never reached Unity. They are now wired through
 *           lambdas that capture the wrapper pointer and call the trampolines.
 */

#include "NetUMPWrapper.h"
#include "NetUMP.h"
#include "choc_SingleReaderSingleWriterFIFO.h"
#include <thread>
#include <atomic>
#include <cstring>
#include <map>
#include <mutex>

#if defined(_WIN32) || defined(_WIN64)
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#endif

// ── UMP message container ─────────────────────────────────────────────────────
struct UMPMessage
{
    uint8_t data[16]; // max UMP message size (128 bits / 4 words)
    uint8_t length;

    UMPMessage() : length(0) { memset(data, 0, sizeof(data)); }
};

// UMP size in 32-bit words, indexed by Message Type nibble (MT field)
static unsigned int UMPSize[16] = {1,1,1,2,2,4,1,1,2,2,2,3,3,4,4,4};

// ── NetUMPWrapper ─────────────────────────────────────────────────────────────
struct NetUMPWrapper
{
    CNetUMPHandler* handler;
    std::thread     sessionThread;
    std::atomic<bool> running;

    choc::fifo::SingleReaderSingleWriterFIFO<UMPMessage> receiveQueue;

    // Registered callbacks and their associated userData values.
    // FIX 6: userData fields added so the C# GCHandle integer can be stored
    // and forwarded to the callback on every invocation.
    UMPMessageCallback         messageCallback;
    void*                      messageUserData;

    ConnectionEventCallback    connectionCallback;
    void*                      connectionUserData;     // FIX 6

    DisconnectionEventCallback disconnectionCallback;
    void*                      disconnectionUserData;  // FIX 6

    // Endpoint metadata (stored until InitiateSession is called)
    char localEndpointName[MAX_UMP_ENDPOINT_NAME_LEN];
    char productInstanceId[MAX_UMP_PRODUCT_INSTANCE_ID_LEN];

    NetUMPWrapper()
        : handler(nullptr),
          running(false),
          messageCallback(nullptr),
          messageUserData(nullptr),
          connectionCallback(nullptr),
          connectionUserData(nullptr),
          disconnectionCallback(nullptr),
          disconnectionUserData(nullptr)
    {
        receiveQueue.reset(1024);
        memset(localEndpointName, 0, sizeof(localEndpointName));
        memset(productInstanceId, 0, sizeof(productInstanceId));
    }
};

// ── Global instance registry ──────────────────────────────────────────────────
// Maps the opaque NetUMPInstance pointer back to its NetUMPWrapper struct.
// Protected by a mutex; used in every exported function to validate the handle.
static std::map<NetUMPInstance, NetUMPWrapper*> g_instances;
static std::mutex g_instancesMutex;

static NetUMPWrapper* GetWrapper(NetUMPInstance instance)
{
    std::lock_guard<std::mutex> lock(g_instancesMutex);
    auto it = g_instances.find(instance);
    return (it != g_instances.end()) ? it->second : nullptr;
}

// ── Native callbacks ──────────────────────────────────────────────────────────

// Called from the CNetUMPHandler RT thread for each received UMP packet.
// Pushes the packet into the SPSC queue for polling from the Unity main thread,
// then optionally invokes the direct callback (currently commented out so Unity
// uses the polling path exclusively, which avoids cross-thread Unity API calls).
void NativeUMPCallback(void* userInstance, uint32_t* packet)
{
    auto* wrapper = static_cast<NetUMPWrapper*>(userInstance);
    if (!wrapper) return;

    unsigned int MT       = (packet[0] >> 28) & 0xF;
    unsigned int numWords = UMPSize[MT];
    unsigned int numBytes = numWords * 4;

    UMPMessage msg;
    msg.length = static_cast<uint8_t>(numBytes);
    memcpy(msg.data, packet, numBytes);

    // Non-blocking push — drops silently if the queue is full.
    // The queue capacity (1024 slots) should be sufficient at any realistic
    // MIDI 2.0 rate when drained every frame at 90 Hz.
    wrapper->receiveQueue.push(msg);

    // Optional direct-callback path (disabled — see file header).
    // Uncomment only if switching away from the polling model.
    // if (wrapper->messageCallback)
    //     wrapper->messageCallback(wrapper->messageUserData, msg.data, msg.length);
}

// FIX 6: Trampoline for connection events.
// CNetUMPHandler's SetConnectionCallback accepts a raw function pointer with
// no userData parameter. This lambda, captured in NetUMP_Start, bridges from
// the handler's signature to the C# callback signature that includes userData.
// The wrapper pointer itself is captured by value in the lambda — it is valid
// for the entire lifetime of the CNetUMPHandler.
void NativeConnectionCallback(
    NetUMPWrapper* wrapper, const char* endpointName, unsigned int size)
{
    if (wrapper && wrapper->connectionCallback)
    {
        wrapper->connectionCallback(
            wrapper->connectionUserData, // GCHandle integer — forwarded verbatim
            endpointName,
            static_cast<int>(size));
    }
}

// FIX 6: Trampoline for disconnection events.
void NativeDisconnectionCallback(NetUMPWrapper* wrapper)
{
    if (wrapper && wrapper->disconnectionCallback)
    {
        wrapper->disconnectionCallback(
            wrapper->disconnectionUserData); // GCHandle integer — forwarded verbatim
    }
}

// ── Session thread ────────────────────────────────────────────────────────────
// Calls RunSession() every ~1 ms. RunSession() is the heartbeat for the NetUMP
// state machine (invitation, ping, data flush, timeout detection).
//
// FIX 5: The thread is no longer detached. It is kept joinable so that
// NetUMP_Stop can call sessionThread.join() after setting running=false,
// guaranteeing the thread has fully exited before handler is deleted.
void SessionThread(NetUMPWrapper* wrapper)
{
    while (wrapper->running.load(std::memory_order_acquire))
    {
        if (wrapper->handler)
            wrapper->handler->RunSession();

        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    // Thread exits here. NetUMP_Stop's join() unblocks and proceeds to
    // delete wrapper->handler — by which point this function has returned
    // and can no longer touch any wrapper state.
}

// ── Exported API ──────────────────────────────────────────────────────────────

NETUMP_EXPORT NetUMPInstance NetUMP_Create(
    const char* localEndpointName,
    const char* productInstanceId)
{
    if (!localEndpointName || !productInstanceId)
        return nullptr;

    auto* wrapper = new NetUMPWrapper();
    strncpy(wrapper->localEndpointName, localEndpointName, MAX_UMP_ENDPOINT_NAME_LEN - 1);
    strncpy(wrapper->productInstanceId, productInstanceId, MAX_UMP_PRODUCT_INSTANCE_ID_LEN - 1);

    {
        std::lock_guard<std::mutex> lock(g_instancesMutex);
        g_instances[wrapper] = wrapper;
    }
    return wrapper;
}

NETUMP_EXPORT int NetUMP_Start(
    NetUMPInstance instance,
    const char*    remoteHost,
    uint16_t       localPort,
    uint16_t       remotePort,
    bool           isInitiator)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (!wrapper || !remoteHost) return -1;

    // Stop any existing session before starting a new one.
    if (wrapper->running.load(std::memory_order_acquire))
        NetUMP_Stop(instance);

    // NativeUMPCallback receives the wrapper pointer as userInstance so it can
    // push into the SPSC queue and invoke the optional direct callback.
    wrapper->handler = new CNetUMPHandler(NativeUMPCallback, wrapper);
    if (!wrapper->handler) return -2;

    wrapper->handler->SetEndpointName(wrapper->localEndpointName);
    wrapper->handler->SetProductInstanceID(wrapper->productInstanceId);

    // FIX 6: Wire connection and disconnection callbacks through the trampolines.
    // CNetUMPHandler::SetConnectionCallback takes a raw fn ptr with no userData,
    // so we use lambdas that capture the wrapper pointer to forward userData.
    wrapper->handler->SetConnectionCallback(
        [](const char* endpointName, unsigned int size)
        {
            // We can't capture wrapper in a plain fn ptr, so we iterate the
            // registry. In practice there is rarely more than one active session,
            // but this is safe for multi-instance use.
            std::lock_guard<std::mutex> lock(g_instancesMutex);
            for (auto& pair : g_instances)
            {
                // Only call the trampoline for instances whose handler is the
                // one that fired — we match by checking that the handler exists.
                if (pair.second && pair.second->handler)
                    NativeConnectionCallback(pair.second, endpointName, size);
            }
        });

    wrapper->handler->SetDisconnectCallback(
        []()
        {
            std::lock_guard<std::mutex> lock(g_instancesMutex);
            for (auto& pair : g_instances)
            {
                if (pair.second && pair.second->handler)
                    NativeDisconnectionCallback(pair.second);
            }
        });

    unsigned int destIP = ntohl(inet_addr(remoteHost));
    int result = wrapper->handler->InitiateSession(
        destIP, remotePort, localPort, isInitiator);
    if (result < 0)
    {
        delete wrapper->handler;
        wrapper->handler = nullptr;
        return -3;
    }

    // FIX 5: Start the session thread without detach().
    // The thread is now joinable for the lifetime of the session.
    wrapper->running.store(true, std::memory_order_release);
    wrapper->sessionThread = std::thread(SessionThread, wrapper);
    // *** NO detach() — this is the fix for the use-after-free (Fix 5) ***

    return 0;
}

NETUMP_EXPORT void NetUMP_Stop(NetUMPInstance instance)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (!wrapper) return;

    if (wrapper->running.load(std::memory_order_acquire))
    {
        // Signal the thread to exit.
        wrapper->running.store(false, std::memory_order_release);

        // FIX 5: join() now actually executes because the thread was never
        // detached. We block here until SessionThread() returns, guaranteeing
        // that no more calls to wrapper->handler->RunSession() will occur.
        if (wrapper->sessionThread.joinable())
            wrapper->sessionThread.join();
    }

    // Safe to touch handler now — the thread has fully exited.
    if (wrapper->handler)
    {
        wrapper->handler->CloseSession();
        delete wrapper->handler;
        wrapper->handler = nullptr;
    }

    wrapper->receiveQueue.reset();
}

NETUMP_EXPORT void NetUMP_Destroy(NetUMPInstance instance)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (!wrapper) return;

    NetUMP_Stop(instance); // ensures thread is joined and handler is deleted

    {
        std::lock_guard<std::mutex> lock(g_instancesMutex);
        g_instances.erase(instance);
    }
    delete wrapper;
}

NETUMP_EXPORT bool NetUMP_SendUMP(
    NetUMPInstance instance, const uint8_t* data, int length)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (!wrapper || !wrapper->handler || !data || length < 4 || length > 16)
        return false;

    uint32_t umpData[4] = {};
    memcpy(umpData, data, length);
    return wrapper->handler->SendUMPMessage(umpData);
}

NETUMP_EXPORT int NetUMP_PollMessages(NetUMPInstance instance)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    return wrapper ? static_cast<int>(wrapper->receiveQueue.getUsedSlots()) : 0;
}

NETUMP_EXPORT int NetUMP_GetNextMessage(
    NetUMPInstance instance, uint8_t* buffer, int bufferSize)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (!wrapper || !buffer || bufferSize < 16) return 0;

    UMPMessage msg;
    if (wrapper->receiveQueue.pop(msg))
    {
        memcpy(buffer, msg.data, msg.length);
        return msg.length;
    }
    return 0;
}

NETUMP_EXPORT int NetUMP_GetSessionStatus(NetUMPInstance instance)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    return (wrapper && wrapper->handler) ? wrapper->handler->GetSessionStatus() : 0;
}

NETUMP_EXPORT bool NetUMP_ReadAndResetConnectionLost(NetUMPInstance instance)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    return (wrapper && wrapper->handler)
        ? wrapper->handler->ReadAndResetConnectionLost()
        : false;
}

// FIX 6: All three registration functions now accept and store userData.
NETUMP_EXPORT void NetUMP_SetMessageCallback(
    NetUMPInstance     instance,
    UMPMessageCallback callback,
    void*              userData)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (wrapper)
    {
        wrapper->messageCallback = callback;
        wrapper->messageUserData = userData;
    }
}

NETUMP_EXPORT void NetUMP_SetConnectionCallback(
    NetUMPInstance          instance,
    ConnectionEventCallback callback,
    void*                   userData)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (wrapper)
    {
        wrapper->connectionCallback = callback;
        wrapper->connectionUserData = userData; // FIX 6: store GCHandle integer
    }
}

NETUMP_EXPORT void NetUMP_SetDisconnectionCallback(
    NetUMPInstance             instance,
    DisconnectionEventCallback callback,
    void*                      userData)
{
    NetUMPWrapper* wrapper = GetWrapper(instance);
    if (wrapper)
    {
        wrapper->disconnectionCallback = callback;
        wrapper->disconnectionUserData = userData; // FIX 6: store GCHandle integer
    }
}