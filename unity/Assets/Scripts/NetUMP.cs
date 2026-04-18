// NetUMP.cs
// Unity wrapper for the NetUMP native library.
// Handles MIDI 2.0 UMP (Universal MIDI Packet) networking.
//
// FIXES APPLIED (see NetUMP_Fix_Reference.docx for full details)
// ──────────────────────────────────────────────────────────────
// Fix 1 — MonoPInvokeCallback on instance methods → all bridge callbacks are
//          now static inside NetUMPCallbackBridge.cs
// Fix 2 — Static delegates bound to instance state → delegates are readonly
//          static fields assigned at declaration, never conditionally
// Fix 3 — gameObject.name accessed from RT thread → cached in _goName in Awake
// Fix 4 — bool marshalling size mismatch on ARM64 → [MarshalAs(UnmanagedType.I1)]
//          on every bool parameter and return value
// Fix 7 — Delegate GC safety hole → readonly static fields are permanent GC roots
// Fix 8 — lock() contention on dispatcher → replaced Queue+lock with
//          System.Collections.Concurrent.ConcurrentQueue (lock-free)
// Fix 9 — Per-message heap allocation → unsafe fixed buffer + ReadOnlySpan<byte>
//          eliminates new byte[] on every UMP packet in the hot path

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NetUMP
{
    /// <summary>
    /// Unity MonoBehaviour wrapper for the NetUMP native library.
    /// Each instance manages one independent network MIDI 2.0 session.
    ///
    /// Usage:
    ///   1. Attach to a GameObject.
    ///   2. Configure fields in the Inspector (or call Initialize() manually).
    ///   3. Subscribe to OnUMPMessageReceived, OnConnected, OnDisconnected.
    ///   4. Call StartSession() to begin the NetUMP handshake.
    /// </summary>
    public class NetUMPWrapper : MonoBehaviour,
        UMPMessageBridge.IListener,   // receives raw UMP packets from native RT thread
        ConnectionBridge.IListener    // receives connect/disconnect from native RT thread
    {
        // =====================================================================
        // P/Invoke declarations
        // =====================================================================
        // All function signatures must exactly match NetUMPWrapper.h.
        // CallingConvention.Cdecl is mandatory — the C++ side uses the cdecl ABI.
        //
        // FIX 4: Every bool parameter and return value carries
        //   [MarshalAs(UnmanagedType.I1)]
        // Without this, the P/Invoke marshaller defaults to a 4-byte Windows BOOL,
        // but C++ bool is 1 byte. On ARM64 (Quest) the size mismatch silently
        // returns wrong values or corrupts the call stack.
        //
        // FIX 6 (C# side): NetUMP_SetConnectionCallback and
        //   NetUMP_SetDisconnectionCallback now accept an IntPtr userData
        //   parameter so the GCHandle integer can be passed through.
        //   The matching C++ changes are in NetUMPWrapper.cpp / .h.
        // =====================================================================

        #region P/Invoke

        private const string DLL_NAME =
#if !UNITY_EDITOR && (UNITY_IOS || UNITY_WEBGL)
            "__Internal";
#else
            "netump";
#endif

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NetUMP_Create(
            string localEndpointName,
            string productInstanceId);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NetUMP_Start(
            IntPtr instance,
            string remoteHost,
            ushort localPort,
            ushort remotePort,
            // FIX 4: explicit 1-byte bool — matches C++ bool size on all platforms
            [MarshalAs(UnmanagedType.I1)] bool isInitiator);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NetUMP_Stop(IntPtr instance);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NetUMP_Destroy(IntPtr instance);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        // FIX 4: return bool is also 1 byte in C++
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool NetUMP_SendUMP(
            IntPtr instance,
            IntPtr data,   // FIX 9: raw pointer instead of byte[] — avoids pinning alloc
            int    length);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NetUMP_PollMessages(IntPtr instance);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NetUMP_GetNextMessage(
            IntPtr instance,
            IntPtr buffer,   // FIX 9: raw pointer — we pin _receiveBuffer ourselves
            int    bufferSize);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NetUMP_GetSessionStatus(IntPtr instance);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        // FIX 4: return bool
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool NetUMP_ReadAndResetConnectionLost(IntPtr instance);

        // FIX 1/6: callbacks now accept IntPtr userData so the bridge can pass
        // the GCHandle integer. The native signatures in NetUMPWrapper.h must match.
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NetUMP_SetMessageCallback(
            IntPtr instance,
            UMPMessageCallbackDelegate callback,
            IntPtr userData);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NetUMP_SetConnectionCallback(
            IntPtr instance,
            ConnectionEventCallbackDelegate callback,
            IntPtr userData);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NetUMP_SetDisconnectionCallback(
            IntPtr instance,
            DisconnectionEventCallbackDelegate callback,
            IntPtr userData);

        #endregion

        // =====================================================================
        // FIX 1 + 2 + 7: Static readonly delegate fields
        // =====================================================================
        // Delegates are assigned once at the class level, never replaced.
        // "readonly" makes these permanent GC roots — the GC will never collect
        // the delegate objects even between sessions.
        //
        // The actual callback code lives in NetUMPCallbackBridge.cs as static
        // methods decorated with [AOT.MonoPInvokeCallback], which is the only
        // form IL2CPP can expose as a native function pointer.
        // =====================================================================

        private static readonly UMPMessageCallbackDelegate s_messageCallback
            = UMPMessageBridge.BridgeCallback;

        private static readonly ConnectionEventCallbackDelegate s_connectCallback
            = ConnectionBridge.BridgeConnectCallback;

        private static readonly DisconnectionEventCallbackDelegate s_disconnectCallback
            = ConnectionBridge.BridgeDisconnectCallback;

        // =====================================================================
        // Inspector configuration
        // =====================================================================

        [Header("NetUMP Configuration")]
        [SerializeField] private string localEndpointName = "UnityNetUMP";
        [SerializeField] private string productInstanceId = "Unity_001";
        [SerializeField] private string remoteHost        = "127.0.0.1";
        [SerializeField] private ushort localPort         = 5555;
        [SerializeField] private ushort remotePort        = 5555;
        [SerializeField] private bool   isInitiator       = true;
        [SerializeField] private bool   autoInitialize    = true;

        // =====================================================================
        // Public events
        // =====================================================================
        // FIX 9: OnUMPMessageReceived uses ReadOnlySpan<byte> instead of byte[].
        // This avoids allocating a new array for every packet on the hot path.
        // IMPORTANT: the span is only valid for the duration of the callback.
        // Subscribers must NOT store the span. If they need to keep the data,
        // they should copy it: data.ToArray() or MemoryMarshal.ToArray(data).

        /// <summary>
        /// Fired on the Unity main thread for each received UMP packet.
        /// The ReadOnlySpan is only valid during the callback — copy if needed.
        /// </summary>
        public event Action<ReadOnlySpan<byte>> OnUMPMessageReceived;

        /// <summary>Fired on the Unity main thread when a session is established.</summary>
        public event Action<string> OnConnected;

        /// <summary>Fired on the Unity main thread when a session ends.</summary>
        public event Action OnDisconnected;

        // =====================================================================
        // Private state
        // =====================================================================

        private IntPtr _handle  = IntPtr.Zero;
        private bool   _running = false;

        // FIX 3: Cache gameObject.name in Awake() so it can be safely read from
        // any thread (including native RT callbacks). Accessing gameObject.name
        // outside the main thread throws a UnityException.
        private string _goName;

        // Bridge objects own the GCHandles that pin 'this' for native callbacks.
        // They must be disposed AFTER the native callbacks are cancelled.
        private UMPMessageBridge  _messageBridge;
        private ConnectionBridge  _connectionBridge;

        // FIX 8: ConcurrentQueue instead of Queue<Action> + lock.
        // ConcurrentQueue is a lock-free MPSC structure — the native RT thread
        // enqueues without blocking, the main thread dequeues in Update().
        private readonly ConcurrentQueue<Action> _mainThreadQueue
            = new ConcurrentQueue<Action>();

        // FIX 9: Single pre-allocated receive buffer.
        // Pinned with 'fixed' in DrainMessageQueue — no per-frame alloc.
        private readonly byte[] _receiveBuffer = new byte[16]; // max UMP size

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        private void Awake()
        {
            // FIX 3: cache name before any thread can call into this object
            _goName = gameObject.name;
        }

        private void Start()
        {
            if (autoInitialize)
                Initialize();
        }

        private void Update()
        {
            // FIX 8: drain connection events posted from the native RT thread.
            // TryDequeue is lock-free — no stall risk inside the VR frame.
            while (_mainThreadQueue.TryDequeue(out var action))
                action();

            if (_handle == IntPtr.Zero) return;

            // Poll and drain UMP messages — FIX 9 hot path
            int count = NetUMP_PollMessages(_handle);
            if (count > 0)
                DrainMessageQueue(count);

            // Check for connection loss (flag is reset on read by native side)
            if (NetUMP_ReadAndResetConnectionLost(_handle))
            {
                Debug.LogWarning($"[{_goName}] NetUMP: Connection lost");
                OnDisconnected?.Invoke();
            }
        }

        private void OnDestroy()         => Shutdown();
        private void OnApplicationQuit() => Shutdown();

        // =====================================================================
        // IListener implementations
        // Called from the native RT thread via the static bridge callbacks.
        // RULES: no UnityEngine API, no allocations if possible, return fast.
        // =====================================================================

        // UMPMessageBridge.IListener
        // The message callback is currently intentionally minimal — UMP data
        // is retrieved via polling in DrainMessageQueue (Update), which is the
        // lowest-latency zero-alloc path available from the main thread.
        // This hook exists so subclasses or future refactors can switch to a
        // pure push model if the C++ SPSC ring buffer is bypassed.
        void UMPMessageBridge.IListener.OnUMPMessage(IntPtr data, int length)
        {
            // Intentionally empty — polling path in Update() handles UMP data.
            // Add push-path logic here only if you remove the SPSC ring buffer.
        }

        // ConnectionBridge.IListener
        // The string has already been marshalled in the bridge before this call,
        // so 'endpointName' is a safe managed string — no further marshalling needed.
        void ConnectionBridge.IListener.OnConnected(string endpointName)
        {
            // Capture for the closure — endpointName is already a managed string
            var name = endpointName;
            // FIX 8: enqueue to ConcurrentQueue — lock-free, safe from RT thread
            _mainThreadQueue.Enqueue(() =>
            {
                Debug.Log($"[{_goName}] NetUMP: Connected to endpoint '{name}'");
                OnConnected?.Invoke(name);
            });
        }

        void ConnectionBridge.IListener.OnDisconnected()
        {
            _mainThreadQueue.Enqueue(() =>
            {
                Debug.Log($"[{_goName}] NetUMP: Disconnected");
                OnDisconnected?.Invoke();
            });
        }

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// Creates the native NetUMP instance and registers callbacks.
        /// Called automatically on Start() when autoInitialize is true.
        /// </summary>
        public bool Initialize()
        {
            if (_handle != IntPtr.Zero)
            {
                Debug.LogWarning($"[{_goName}] NetUMP: Already initialized");
                return true;
            }

            Debug.Log($"[{_goName}] NetUMP: Creating native instance");

            _handle = NetUMP_Create(localEndpointName, productInstanceId);
            if (_handle == IntPtr.Zero)
            {
                Debug.LogError($"[{_goName}] NetUMP: NetUMP_Create failed");
                return false;
            }

            // Create bridge objects — these pin 'this' via GCHandle.Alloc().
            // They must outlive any native callback invocation.
            _messageBridge    = new UMPMessageBridge(this);
            _connectionBridge = new ConnectionBridge(this);

            // Register the static bridge callbacks with the native library.
            // UserData is the GCHandle integer — the static bridges use it to
            // recover 'this' via GCHandle.FromIntPtr(userData).Target.
            NetUMP_SetMessageCallback(
                _handle, s_messageCallback, _messageBridge.UserData);
            NetUMP_SetConnectionCallback(
                _handle, s_connectCallback, _connectionBridge.UserData);
            NetUMP_SetDisconnectionCallback(
                _handle, s_disconnectCallback, _connectionBridge.UserData);

            Debug.Log($"[{_goName}] NetUMP: Initialized successfully");
            return true;
        }

        /// <summary>
        /// Initiates the NetUMP session handshake with the remote host.
        /// Call after Initialize().
        /// </summary>
        public bool StartSession()
        {
            if (_handle == IntPtr.Zero)
            {
                Debug.LogError($"[{_goName}] NetUMP: Call Initialize() first");
                return false;
            }

            Debug.Log($"[{_goName}] NetUMP: Starting session → {remoteHost}:{remotePort}");

            int result = NetUMP_Start(
                _handle, remoteHost, localPort, remotePort, isInitiator);

            if (result < 0)
            {
                Debug.LogError($"[{_goName}] NetUMP: NetUMP_Start failed (code {result})");
                return false;
            }

            _running = true;
            Debug.Log($"[{_goName}] NetUMP: Session started");
            return true;
        }

        /// <summary>
        /// Stops the current session and destroys the native instance.
        /// Safe to call multiple times.
        /// </summary>
        public void Shutdown()
        {
            if (_handle == IntPtr.Zero) return;

            Debug.Log($"[{_goName}] NetUMP: Shutting down");

            // 1. Stop the native session (joins the C++ session thread — FIX 5).
            //    After this returns, no more native callbacks will fire.
            if (_running)
            {
                NetUMP_Stop(_handle);
                _running = false;
            }

            // 2. Release GCHandles AFTER native callbacks are silenced.
            //    Disposing while native code still holds the function pointer
            //    would unpin the managed object prematurely — use-after-free.
            _messageBridge?.Dispose();    _messageBridge    = null;
            _connectionBridge?.Dispose(); _connectionBridge = null;

            // 3. Free the native instance last.
            NetUMP_Destroy(_handle);
            _handle = IntPtr.Zero;
        }

        /// <summary>
        /// Stops and restarts the session with current configuration.
        /// </summary>
        public bool Restart()
        {
            if (_handle == IntPtr.Zero)
                return Initialize() && StartSession();

            Debug.Log($"[{_goName}] NetUMP: Restarting session");

            NetUMP_Stop(_handle);
            _running = false;

            int result = NetUMP_Start(
                _handle, remoteHost, localPort, remotePort, isInitiator);

            if (result < 0)
            {
                Debug.LogError($"[{_goName}] NetUMP: Restart failed (code {result})");
                return false;
            }

            _running = true;
            return true;
        }

        /// <summary>
        /// Sends a UMP message over the network.
        /// </summary>
        /// <param name="data">UMP bytes (4, 8, 12, or 16 bytes).</param>
        public unsafe bool SendUMP(ReadOnlySpan<byte> data)
        {
            if (_handle == IntPtr.Zero)
            {
                Debug.LogWarning($"[{_goName}] NetUMP: Cannot send — not initialized");
                return false;
            }

            if (data.Length < 4 || data.Length > 16 || data.Length % 4 != 0)
            {
                Debug.LogError(
                    $"[{_goName}] NetUMP: Invalid UMP size {data.Length} (must be 4/8/12/16)");
                return false;
            }

            // FIX 9: pin the caller's span — no heap allocation
            fixed (byte* ptr = data)
                return NetUMP_SendUMP(_handle, (IntPtr)ptr, data.Length);
        }

        /// <summary>Convenience overload for byte[] callers.</summary>
        public bool SendUMP(byte[] data)
            => data != null ? SendUMP(data.AsSpan()) : false;

        // =====================================================================
        // Status helpers
        // =====================================================================

        /// <returns>0 = closed, 1 = inviting, 3 = opened</returns>
        public int GetSessionStatus()
            => _handle != IntPtr.Zero ? NetUMP_GetSessionStatus(_handle) : 0;

        public string GetSessionStatusString() => GetSessionStatus() switch
        {
            0 => "Closed",
            1 => "Inviting",
            3 => "Opened",
            _ => "Unknown"
        };

        public bool IsInitialized => _handle != IntPtr.Zero;
        public bool IsRunning     => _running;

        // =====================================================================
        // FIX 9: Zero-allocation UMP drain
        // =====================================================================
        // _receiveBuffer is allocated once in the field initializer.
        // 'fixed' pins it for the duration of the loop — no per-message alloc.
        // NetUMP_GetNextMessage writes into the pinned pointer.
        // OnUMPMessageReceived receives a ReadOnlySpan<byte> view of the buffer.
        //
        // CONTRACT FOR SUBSCRIBERS: the span is only valid during the invocation.
        // Subscribers that need to keep the data must copy it (data.ToArray()).
        // =====================================================================
        private unsafe void DrainMessageQueue(int count)
        {
            fixed (byte* buf = _receiveBuffer)
            {
                var ptr = (IntPtr)buf;
                for (int i = 0; i < count; i++)
                {
                    int length = NetUMP_GetNextMessage(_handle, ptr, _receiveBuffer.Length);
                    if (length <= 0) break;

                    // Zero-allocation view — valid only during this Invoke
                    var span = new ReadOnlySpan<byte>(buf, length);
                    OnUMPMessageReceived?.Invoke(span);
                }
            }
        }

        // =====================================================================
        // Static UMP helpers
        // =====================================================================

        /// <summary>
        /// Returns the Message Type (MT) nibble from the first UMP word.
        /// MT determines the packet size: UMPSize[MT] × 4 bytes.
        /// </summary>
        public static int GetMessageType(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return -1;
            uint firstWord = BitConverter.ToUInt32(data);
            return (int)((firstWord >> 28) & 0xF);
        }

        /// <summary>Interprets UMP bytes as an array of 32-bit words.</summary>
        public static uint[] BytesToWords(ReadOnlySpan<byte> data)
        {
            var words = new uint[data.Length / 4];
            for (int i = 0; i < words.Length; i++)
                words[i] = BitConverter.ToUInt32(data.Slice(i * 4, 4));
            return words;
        }
    }
}