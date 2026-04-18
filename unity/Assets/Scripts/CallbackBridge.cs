// CallbackBridge.cs
// IL2CPP-safe bridge between unmanaged NetUMP callbacks and managed Unity code.
//
// RATIONALE
// ---------
// IL2CPP (used on Android/Quest and iOS) compiles C# ahead-of-time. It can
// only expose *static* methods to unmanaged code as function pointers.
// Instance delegates are not supported: handing a closed-over instance method
// to a native library as a callback will crash under IL2CPP even if it works
// fine on Windows (Mono).
//
// SOLUTION — the GCHandle bridge pattern (from RTMidi by Keijiro Takahashi)
// -------------------------------------------------------------------------
// 1. Pin the managed listener object with GCHandle.Alloc().
//    The GC will never move or collect it while pinned.
// 2. Convert the handle to an IntPtr (a stable integer) and pass it to the
//    native library as the "userData" / "instance" opaque pointer.
// 3. The static bridge callback receives that integer, recovers the original
//    managed object via GCHandle.FromIntPtr(), and calls the interface method.
// 4. The concrete wrapper class implements the interface, so the call reaches
//    the right instance without any global state.
// 5. Dispose() calls GCHandle.Free() once the native callback is cancelled,
//    unpinning the object so the GC can manage it normally again.
//
// CALL FLOW
//   Native RunSession thread
//     └─→ NativeUMPCallback(userData, packet)          [C++ → C# boundary]
//           └─→ UMPMessageBridge.BridgeCallback()      [static — IL2CPP safe]
//                 └─→ GCHandle.FromIntPtr(userData)    [recover pinned object]
//                       └─→ IListener.OnUMPMessage()   [interface dispatch]
//                             └─→ NetUMPWrapper method [instance logic]

using System;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

namespace NetUMP
{
    // -------------------------------------------------------------------------
    // Delegate types — must exactly match the C++ function pointer signatures
    // in NetUMPWrapper.h. CallingConvention.Cdecl is required for all platforms.
    // -------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void UMPMessageCallbackDelegate(
        IntPtr userData,       // GCHandle integer passed back from native
        IntPtr data,           // pointer to raw UMP bytes
        int    length);        // byte count (4, 8, 12, or 16)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ConnectionEventCallbackDelegate(
        IntPtr userData,       // GCHandle integer
        IntPtr endpointName,   // null-terminated C string
        int    nameLength);    // byte count of the name

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DisconnectionEventCallbackDelegate(
        IntPtr userData);      // GCHandle integer

    // =========================================================================
    // UMPMessageBridge
    // Relays NativeUMPCallback → IListener.OnUMPMessage
    // =========================================================================
    internal sealed class UMPMessageBridge : IDisposable
    {
        // ── Listener interface ────────────────────────────────────────────────
        // NetUMPWrapper implements this. The bridge only knows about the
        // interface, never about the concrete class.
        public interface IListener
        {
            /// <summary>
            /// Called from the native RT thread. Keep work minimal.
            /// Do NOT access any UnityEngine API here.
            /// </summary>
            void OnUMPMessage(IntPtr data, int length);
        }

        // ── GCHandle that pins the listener ───────────────────────────────────
        GCHandle _handle;

        public UMPMessageBridge(IListener listener)
            => _handle = GCHandle.Alloc(listener); // pin — GC will not collect/move

        public void Dispose()
        {
            // Must be called AFTER the native side has cancelled the callback.
            // Freeing while native code still holds the function pointer is UB.
            if (_handle.IsAllocated)
                _handle.Free();
        }

        // ── Properties consumed by NetUMPWrapper when registering callbacks ───
        // Callback: the static method pointer passed to the native library.
        // UserData: the GCHandle integer passed as "userData" / "instance".
        public UMPMessageCallbackDelegate Callback => BridgeCallback;
        public IntPtr UserData => GCHandle.ToIntPtr(_handle);

        // ── Static bridge — the only thing IL2CPP can expose natively ─────────
        [AOT.MonoPInvokeCallback(typeof(UMPMessageCallbackDelegate))]
        public static void BridgeCallback(IntPtr userData, IntPtr data, int length)
        {
            try
            {
                var listener = (IListener)GCHandle.FromIntPtr(userData).Target;
                listener.OnUMPMessage(data, length);
            }
            catch (Exception e)
            {
                // Never let an exception propagate into unmanaged code.
                Debug.LogError($"[NetUMP] Exception in UMP message callback: {e}");
            }
        }
    }

    // =========================================================================
    // ConnectionBridge
    // Relays NativeConnectionCallback / NativeDisconnectionCallback
    //   → IListener.OnConnected / IListener.OnDisconnected
    // Both connection events share one bridge and one GCHandle because they
    // always belong to the same listener instance.
    // =========================================================================
    internal sealed class ConnectionBridge : IDisposable
    {
        // ── Listener interface ────────────────────────────────────────────────
        public interface IListener
        {
            /// <summary>Called from the native RT thread on session open.</summary>
            void OnConnected(string endpointName);

            /// <summary>Called from the native RT thread on session close.</summary>
            void OnDisconnected();
        }

        // ── GCHandle ──────────────────────────────────────────────────────────
        GCHandle _handle;

        public ConnectionBridge(IListener listener)
            => _handle = GCHandle.Alloc(listener);

        public void Dispose()
        {
            if (_handle.IsAllocated)
                _handle.Free();
        }

        // ── Properties ────────────────────────────────────────────────────────
        public ConnectionEventCallbackDelegate    ConnectCallback    => BridgeConnectCallback;
        public DisconnectionEventCallbackDelegate DisconnectCallback => BridgeDisconnectCallback;
        public IntPtr UserData => GCHandle.ToIntPtr(_handle);

        // ── Static bridges ────────────────────────────────────────────────────
        [AOT.MonoPInvokeCallback(typeof(ConnectionEventCallbackDelegate))]
        public static void BridgeConnectCallback(
            IntPtr userData, IntPtr endpointName, int nameLength)
        {
            try
            {
                var listener = (IListener)GCHandle.FromIntPtr(userData).Target;
                // Marshal the C string to a managed string here, on the RT thread,
                // before the pointer potentially becomes invalid. The resulting
                // managed string is safe to capture in a closure.
                var name = nameLength > 0
                    ? Marshal.PtrToStringAnsi(endpointName, nameLength)
                    : Marshal.PtrToStringAnsi(endpointName) ?? string.Empty;
                listener.OnConnected(name);
            }
            catch (Exception e)
            {
                Debug.LogError($"[NetUMP] Exception in connection callback: {e}");
            }
        }

        [AOT.MonoPInvokeCallback(typeof(DisconnectionEventCallbackDelegate))]
        public static void BridgeDisconnectCallback(IntPtr userData)
        {
            try
            {
                var listener = (IListener)GCHandle.FromIntPtr(userData).Target;
                listener.OnDisconnected();
            }
            catch (Exception e)
            {
                Debug.LogError($"[NetUMP] Exception in disconnection callback: {e}");
            }
        }
    }
}