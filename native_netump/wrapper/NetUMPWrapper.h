/*
 *  NetUMPWrapper.h
 *  Unity-facing C API for the NetUMP session library.
 *
 *  FIXES APPLIED
 *  ─────────────
 *  Fix 5  — sessionThread.detach() removed in .cpp; join() now works correctly.
 *  Fix 6  — NetUMP_SetConnectionCallback and NetUMP_SetDisconnectionCallback now
 *            accept a void* userData parameter so the C# GCHandle integer can be
 *            threaded through to the callback invocation. Without userData there
 *            was no way for a raw function pointer callback to know which managed
 *            wrapper instance it belonged to.
 */

#ifndef __NETUMP_WRAPPER_H__
#define __NETUMP_WRAPPER_H__

#include <stdint.h>

// ── Export macro ──────────────────────────────────────────────────────────────
#if defined(_WIN32) || defined(_WIN64)
    #define NETUMP_EXPORT extern "C" __declspec(dllexport)
#elif defined(__APPLE__) || defined(__linux__)
    #define NETUMP_EXPORT extern "C" __attribute__((visibility("default")))
#else
    #define NETUMP_EXPORT extern "C"
#endif

// ── Opaque instance handle ────────────────────────────────────────────────────
// The C# side stores this as an IntPtr. Never dereference it from managed code.
typedef void* NetUMPInstance;

// ── Callback types ────────────────────────────────────────────────────────────
// FIX 6: All three callbacks now carry a void* userData as their first argument.
// The C# bridge passes a GCHandle integer as userData when registering; the
// static bridge callback uses GCHandle.FromIntPtr(userData) to recover the
// pinned managed listener and dispatch the call to the correct instance.
// This mirrors the pattern used by RTMidi (Keijiro) and is the standard
// IL2CPP-safe approach for routing native callbacks to managed instances.

// Called from the native RT thread for each received UMP packet.
// userData  — opaque value supplied when the callback was registered (GCHandle integer on C# side)
// data      — pointer to raw UMP bytes (4, 8, 12, or 16 bytes)
// length    — byte count
typedef void (*UMPMessageCallback)(
    void*          userData,
    const uint8_t* data,
    int            length);

// Called from the native RT thread when a session is successfully opened.
// userData     — same opaque value as above
// endpointName — null-terminated UTF-8 string; valid only during the call
// nameLength   — byte count, not including the null terminator
typedef void (*ConnectionEventCallback)(
    void*       userData,
    const char* endpointName,
    int         nameLength);

// Called from the native RT thread when a session is closed for any reason.
// userData — same opaque value as above
typedef void (*DisconnectionEventCallback)(
    void* userData);

// ── Lifecycle ─────────────────────────────────────────────────────────────────

// Create a new NetUMP wrapper. Must be called before any other function.
// Returns NULL on allocation failure.
NETUMP_EXPORT NetUMPInstance NetUMP_Create(
    const char* localEndpointName,
    const char* productInstanceId);

// Initialise network resources and begin the session handshake.
// Returns 0 on success, negative on error:
//   -1  invalid arguments
//   -2  handler allocation failure
//   -3  socket / InitiateSession failure
NETUMP_EXPORT int NetUMP_Start(
    NetUMPInstance instance,
    const char*    remoteHost,
    uint16_t       localPort,
    uint16_t       remotePort,
    bool           isInitiator);

// Stop the session. Blocks until the session thread has exited (FIX 5).
// Safe to call if the session was never started.
NETUMP_EXPORT void NetUMP_Stop(NetUMPInstance instance);

// Stop the session and free all resources. The instance is invalid afterwards.
NETUMP_EXPORT void NetUMP_Destroy(NetUMPInstance instance);

// ── Data I/O ──────────────────────────────────────────────────────────────────

// Enqueue a UMP message for transmission.
// data   — pointer to 4–16 bytes of UMP data in native byte order
// length — byte count (must be 4, 8, 12, or 16)
// Returns true if the message was accepted into the send queue.
NETUMP_EXPORT bool NetUMP_SendUMP(
    NetUMPInstance instance,
    const uint8_t* data,
    int            length);

// Returns the number of UMP messages waiting in the receive queue.
// Call from the Unity main thread each frame before NetUMP_GetNextMessage.
NETUMP_EXPORT int NetUMP_PollMessages(NetUMPInstance instance);

// Copy the next message from the receive queue into buffer.
// buffer     — caller-allocated buffer of at least 16 bytes
// bufferSize — size of buffer in bytes (must be >= 16)
// Returns the number of bytes written, or 0 if the queue is empty.
NETUMP_EXPORT int NetUMP_GetNextMessage(
    NetUMPInstance instance,
    uint8_t*       buffer,
    int            bufferSize);

// ── Status ────────────────────────────────────────────────────────────────────

// Returns the current session state:
//   0 — closed
//   1 — inviting (handshake in progress)
//   3 — open (UMP data can be exchanged)
NETUMP_EXPORT int NetUMP_GetSessionStatus(NetUMPInstance instance);

// Returns true if the connection was lost since the last call.
// The flag is reset on read — this returns true at most once per loss event.
NETUMP_EXPORT bool NetUMP_ReadAndResetConnectionLost(NetUMPInstance instance);

// ── Callback registration ─────────────────────────────────────────────────────
// FIX 6: Every registration function now takes a userData pointer that is
// passed back verbatim as the first argument to the callback. On the C# side
// this is a GCHandle integer; the static bridge uses it to find the managed
// listener. On pure C++ clients any pointer-sized value works.

// Register a callback for received UMP messages.
// The callback is invoked from the native RT thread — keep it minimal.
NETUMP_EXPORT void NetUMP_SetMessageCallback(
    NetUMPInstance     instance,
    UMPMessageCallback callback,
    void*              userData);

// Register a callback for session-open events.
NETUMP_EXPORT void NetUMP_SetConnectionCallback(
    NetUMPInstance          instance,
    ConnectionEventCallback callback,
    void*                   userData);

// Register a callback for session-close events.
NETUMP_EXPORT void NetUMP_SetDisconnectionCallback(
    NetUMPInstance             instance,
    DisconnectionEventCallback callback,
    void*                      userData);

#endif // __NETUMP_WRAPPER_H__