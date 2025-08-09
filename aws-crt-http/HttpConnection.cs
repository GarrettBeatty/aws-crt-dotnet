/**
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * SPDX-License-Identifier: Apache-2.0.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;

using Aws.Crt.IO;

namespace Aws.Crt.Http
{
    public enum HeaderBlock 
    {
        Main = 0,
        Informational = 1,
        Trailing = 2
    }

    public class StreamResult
    {
        public int ErrorCode { get; private set; }

        internal StreamResult(int errorCode)
        {
            ErrorCode = errorCode;
        }
    }

    public abstract class HttpClientStreamEventArgs : EventArgs
    {
        public HttpClientStream Stream { get; private set; }

        internal HttpClientStreamEventArgs(HttpClientStream stream)
        {
            Stream = stream;
        }
    }

    public class IncomingHeadersEventArgs : HttpClientStreamEventArgs
    {
        public HttpHeader[] Headers { get; private set; }
        public HeaderBlock Block { get; private set; }

        internal IncomingHeadersEventArgs(HttpClientStream stream, HeaderBlock block, HttpHeader[] headers)
            : base(stream)
        {
            Headers = headers;
            Block = block;
        }
    }

    public class IncomingHeadersDoneEventArgs : HttpClientStreamEventArgs
    {
        public HeaderBlock Block { get; private set; }

        internal IncomingHeadersDoneEventArgs(HttpClientStream stream, HeaderBlock block)
            : base(stream)
        {
            Block = block;
        }
    }

    public class IncomingBodyEventArgs : HttpClientStreamEventArgs
    {
        public byte[] Data { get; private set; }

        internal IncomingBodyEventArgs(HttpClientStream stream, byte[] data)
            : base(stream)
        {
            Data = data;
        }
    }

    public class StreamCompleteEventArgs : HttpClientStreamEventArgs
    {
        public int ErrorCode { get; private set; }

        internal StreamCompleteEventArgs(HttpClientStream stream, int errorCode)
            : base(stream)
        {
            ErrorCode = errorCode;
        }
    }

    public class HttpResponseStreamHandler
    {
        public event EventHandler<StreamCompleteEventArgs> StreamComplete;
        public event EventHandler<IncomingHeadersEventArgs> IncomingHeaders;
        public event EventHandler<IncomingHeadersDoneEventArgs> IncomingHeadersDone;
        public event EventHandler<IncomingBodyEventArgs> IncomingBody;

        internal void Validate()
        {
            if (IncomingHeaders == null)
                throw new ArgumentNullException("IncomingHeaders");
            if (StreamComplete == null)
                throw new ArgumentNullException("StreamComplete");
        }

        internal void OnStreamComplete(HttpClientStream stream, int errorCode)
        {
            StreamComplete?.Invoke(stream, new StreamCompleteEventArgs(stream, errorCode));
        }

        internal void OnIncomingHeaders(HttpClientStream stream, HeaderBlock block, HttpHeader[] headers)
        {
            IncomingHeaders?.Invoke(stream, new IncomingHeadersEventArgs(stream, block, headers));
        }

        internal void OnIncomingHeadersDone(HttpClientStream stream, HeaderBlock block)
        {
            IncomingHeadersDone?.Invoke(stream, new IncomingHeadersDoneEventArgs(stream, block));
        }

        internal void OnIncomingBody(HttpClientStream stream, byte[] data)
        {
            var e = new IncomingBodyEventArgs(stream, data);
            IncomingBody?.Invoke(stream, e);
        }
    }

    public sealed class HttpRequest
    {
        public string Method { get; set; }
        public string Uri { get; set; }
        public HttpHeader[] Headers { get; set; }
        public Stream BodyStream { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
    public struct HttpHeader
    {
        [MarshalAs(UnmanagedType.LPStr)]
        private string name;

        [MarshalAs(UnmanagedType.LPStr)]
        private string value;

        private Int32 nameSize;

        private Int32 valueSize;

        public String Name
        {
            get { return this.name; }
            set {
                this.name = value;
                this.nameSize = this.name.Length;
            }
        }

        public String Value
        {
            get { return this.value; }
            set {
                this.value = value;
                this.valueSize = this.value.Length;
            }
        }

        public HttpHeader(string name, string value)
        {
            this.name = name;
            this.value = value;
            this.nameSize = name.Length;
            this.valueSize = value.Length;
        }
    }

    /*
    * Note: Mono does not support marshalling of arrays with non-blittable
    * members from native (but it can marshall them to native). 
    * HttpHeaderNative is a blittable version of HttpHeader
    * (both can marshall aws_dotnet_http_header from native).
    * Use HttpHeaderNative in callbacks from native that need to pass array of
    * headers. And HttpHeader otherwise.
    * Note: HttpHeaderNative holds on to native string pointer, so its only valid
    * while native pointer is valid, i.e. within the callback, and needs to be
    * transformed to HttpHeader if data is used outside of callback scope.
    */
    [StructLayout(LayoutKind.Sequential)]
    public struct HttpHeaderNative
    {
        private IntPtr name;

        private IntPtr value;

        private Int32 nameSize;

        private Int32 valueSize;

        public String Name
        {
            get { return Marshal.PtrToStringAnsi(name, nameSize); }
        }

        public String Value
        {
            get { return Marshal.PtrToStringAnsi(value, valueSize); }
        }
    }

    public abstract class HttpStream
    {
        [SecuritySafeCritical]
        internal static class API
        {
            internal delegate void OnIncomingHeadersNative(
                                    Int32 responseCode,
                                    [MarshalAs(UnmanagedType.I4)] HeaderBlock headerBlock,
                                    [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=3)] HttpHeaderNative[] headers, 
                                    UInt32 count);
            internal delegate void OnIncomingHeaderBlockDoneNative(
                                    [MarshalAs(UnmanagedType.I4)] HeaderBlock headerBlock);
            internal delegate void OnIncomingBodyNative(
                                    [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=1)] byte[] buffer,
                                    UInt64 size);
            internal delegate void OnStreamCompleteNative(int errorCode);

            private static LibraryHandle library = new LibraryHandle();

            [UnmanagedFunctionPointerAttribute(CallingConvention.Cdecl)]
            public delegate Handle aws_dotnet_http_stream_new_from_manager(
                                    IntPtr raw_connection,
                                    [MarshalAs(UnmanagedType.LPStr)] string method,
                                    [MarshalAs(UnmanagedType.LPStr)] string uri,
                                    [In] HttpHeader[] headers,
                                    UInt32 header_count,
                                    [In] CrtStreamWrapper.DelegateTable streamDelegateTable,
                                    OnIncomingHeadersNative onIncomingHeaders,
                                    OnIncomingHeaderBlockDoneNative onIncomingHeaderBlockDone,
                                    OnIncomingBodyNative onIncomingBody,
                                    OnStreamCompleteNative onStreamComplete);

            [UnmanagedFunctionPointerAttribute(CallingConvention.Cdecl)]
            public delegate void aws_dotnet_http_stream_destroy(IntPtr stream);

            [UnmanagedFunctionPointerAttribute(CallingConvention.Cdecl)]
            public delegate void aws_dotnet_http_stream_update_window(IntPtr stream, UInt64 incrementSize);

            [UnmanagedFunctionPointerAttribute(CallingConvention.Cdecl)]
            internal delegate void aws_dotnet_http_stream_activate(IntPtr stream);

            public static aws_dotnet_http_stream_new_from_manager make_new = NativeAPI.Bind<aws_dotnet_http_stream_new_from_manager>();
            public static aws_dotnet_http_stream_destroy destroy = NativeAPI.Bind<aws_dotnet_http_stream_destroy>();
            public static aws_dotnet_http_stream_update_window update_window = NativeAPI.Bind<aws_dotnet_http_stream_update_window>();

            public static aws_dotnet_http_stream_activate activate = NativeAPI.Bind<aws_dotnet_http_stream_activate>();
        }

        public class Handle : CRT.Handle
        {
            protected override bool ReleaseHandle()
            {
                API.destroy(handle);
                return true;
            }
        }

        public Handle NativeHandle { get; set; }

        public HttpClientConnection Connection { get; protected set; }

        public HttpStream(HttpClientConnection connection) 
        {
            this.Connection = connection;
        }

        public void UpdateWindow(ulong incrementSize)
        {
            API.update_window(NativeHandle.DangerousGetHandle(), incrementSize);
        }
    }

    public sealed class HttpClientStream : HttpStream
    {
        public int ResponseStatusCode { get; private set; }

        // Reference to options used to create this stream, which keeps the callbacks alive
        // for the duration of the stream
        private HttpRequest request;

        private CrtStreamWrapper requestBodyStream;

        private HttpResponseStreamHandler responseHandler;

        // References to native callbacks to keep them alive for the duration of the stream
        private API.OnIncomingHeadersNative onIncomingHeaders;
        private API.OnIncomingHeaderBlockDoneNative onIncomingHeaderBlockDone;
        private API.OnIncomingBodyNative onIncomingBody;
        private API.OnStreamCompleteNative onStreamComplete;

        internal HttpClientStream(HttpClientConnection connection, HttpRequest request, HttpResponseStreamHandler responseHandler)
            : base(connection)
        {       
            // System.Console.WriteLine($"[CRT-STREAM] HttpClientStream constructor called");
            // System.Console.WriteLine($"[CRT-STREAM] Connection: {connection?.GetType().Name ?? "null"}");
            // System.Console.WriteLine($"[CRT-STREAM] Request method: {request?.Method ?? "null"}");
            // System.Console.WriteLine($"[CRT-STREAM] Request URI: {request?.Uri ?? "null"}");
            
            if (connection?.NativeHandle != null)
            {
                // System.Console.WriteLine($"[CRT-STREAM] Connection handle valid: {!connection.NativeHandle.IsInvalid}");
                // System.Console.WriteLine($"[CRT-STREAM] Connection handle address: 0x{connection.NativeHandle.DangerousGetHandle():X}");
            }
            else
            {
                // System.Console.WriteLine($"[CRT-STREAM] ERROR: Connection or connection handle is null");
            }
            
            // System.Console.WriteLine($"[CRT-STREAM] Validating response handler...");
            responseHandler.Validate();
            // System.Console.WriteLine($"[CRT-STREAM] Response handler validated");

            this.request = request;
            this.requestBodyStream = new CrtStreamWrapper(request.BodyStream);
            // System.Console.WriteLine($"[CRT-STREAM] Request body stream wrapper created");

            this.responseHandler = responseHandler;

            // System.Console.WriteLine($"[CRT-STREAM] Setting up native callbacks...");
            // Wrap the native callbacks to bind this stream to them as the first argument
            onIncomingHeaders = (responseCode, block, headers, headerCount) =>
            {
                if (ResponseStatusCode == 0) {
                    ResponseStatusCode = responseCode;
                }
                responseHandler.OnIncomingHeaders(this, block,
                    Array.ConvertAll(headers, header => new HttpHeader(header.Name, header.Value)));
            };

            onIncomingHeaderBlockDone = (block) =>
            {
                responseHandler.OnIncomingHeadersDone(this, block);
            };

            onIncomingBody = (byte[] data, ulong size) =>
            {
                responseHandler.OnIncomingBody(this, data);
            };

            onStreamComplete = (errorCode) =>
            {
                responseHandler.OnStreamComplete(this, errorCode);
            };
            
            // System.Console.WriteLine($"[CRT-STREAM] Native callbacks configured");
            // System.Console.WriteLine($"[CRT-STREAM] Calling native API make_new...");
            // System.Console.WriteLine($"[CRT-STREAM] Parameters:");
            // System.Console.WriteLine($"[CRT-STREAM]   connection handle: 0x{connection.NativeHandle.DangerousGetHandle():X}");
            // System.Console.WriteLine($"[CRT-STREAM]   method: {request.Method}");
            // System.Console.WriteLine($"[CRT-STREAM]   uri: {request.Uri}");
            // System.Console.WriteLine($"[CRT-STREAM]   header count: {(UInt32)(request.Headers?.Length ?? 0)}");
            
            try
            {
                NativeHandle = API.make_new(
                    connection.NativeHandle.DangerousGetHandle(),
                    request.Method,
                    request.Uri,
                    request.Headers,
                    (UInt32)(request.Headers?.Length ?? 0),
                    requestBodyStream.Delegates,
                    onIncomingHeaders,
                    onIncomingHeaderBlockDone,
                    onIncomingBody,
                    onStreamComplete);
                    
                // System.Console.WriteLine($"[CRT-STREAM] Native API make_new completed successfully");
                // System.Console.WriteLine($"[CRT-STREAM] Stream native handle: 0x{NativeHandle?.DangerousGetHandle():X}");
            }
            catch (Exception ex)
            {
                // System.Console.WriteLine($"[CRT-STREAM] ERROR: Native API make_new failed: {ex.Message}");
                // System.Console.WriteLine($"[CRT-STREAM] Exception type: {ex.GetType().Name}");
                // System.Console.WriteLine($"[CRT-STREAM] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public void Activate() 
        {
            API.activate(NativeHandle.DangerousGetHandle());
        }
    }

    public sealed class ConnectionSetupEventArgs : EventArgs
    {
        public int ErrorCode { get; private set; }

        internal ConnectionSetupEventArgs(int errorCode)
        {
            ErrorCode = errorCode;
        }
    }

    public sealed class ConnectionShutdownEventArgs : EventArgs
    {
        public int ErrorCode { get; private set; }

        internal ConnectionShutdownEventArgs(int errorCode)
        {
            ErrorCode = errorCode;
        }
    }

    public sealed class HttpClientConnectionOptions
    {
        public ClientBootstrap ClientBootstrap { get; set; }
        public uint InitialWindowSize { get; set; }
        public string HostName { get; set; }
        public UInt16 Port { get; set; }
        public SocketOptions SocketOptions { get; set; }
        public TlsConnectionOptions TlsConnectionOptions { get; set; }
        internal event EventHandler<ConnectionSetupEventArgs> ConnectionSetup;
        public event EventHandler<ConnectionShutdownEventArgs> ConnectionShutdown;

        internal void Validate()
        {
            if (ClientBootstrap == null)
                throw new ArgumentNullException("ClientBootstrap");
            if (ConnectionShutdown == null)
                throw new ArgumentNullException("ConnectionShutdown");
            if (HostName == null)
                throw new ArgumentNullException("HostName");
            if (Port == 0)
                throw new ArgumentOutOfRangeException("Port", Port, "Port must be between 1 and 65535");
        }

        internal void OnConnectionSetup(HttpClientConnection sender, int errorCode)
        {
            ConnectionSetup?.Invoke(sender, new ConnectionSetupEventArgs(errorCode));
        }

        internal void OnConnectionShutdown(HttpClientConnection sender, int errorCode)
        {
            ConnectionShutdown?.Invoke(sender, new ConnectionShutdownEventArgs(errorCode));
        }
    }

    public sealed class HttpClientConnection
    {
        [SecuritySafeCritical]
        internal static class API
        {
            public delegate void OnConnectionSetup(int errorCode);
            public delegate void OnConnectionShutdown(int errorCode);

            static private LibraryHandle library = new LibraryHandle();

            [UnmanagedFunctionPointerAttribute(CallingConvention.Cdecl)]
            public delegate Handle aws_dotnet_http_connection_new(
                                    IntPtr clientBootstrap,
                                    UInt64 initialWindowSize,
                                    [MarshalAs(UnmanagedType.LPStr)] string hostName,
                                    UInt16 port,
                                    IntPtr socketOptions,
                                    IntPtr tlsConnectionOptions,
                                    OnConnectionSetup onSetup,
                                    OnConnectionShutdown onShutdown);

            [UnmanagedFunctionPointerAttribute(CallingConvention.Cdecl)]
            public delegate void aws_dotnet_http_connection_destroy(IntPtr connection);

            public static aws_dotnet_http_connection_new make_new = NativeAPI.Bind<aws_dotnet_http_connection_new>();
            public static aws_dotnet_http_connection_destroy destroy = NativeAPI.Bind<aws_dotnet_http_connection_destroy>();
        }

        internal class Handle : CRT.Handle
        {
            // Constructor for wrapping existing native handle (used by connection manager)
            public Handle(IntPtr existingHandle)
            {
                // System.Console.WriteLine($"[CRT-HANDLE] Creating Handle from existing IntPtr: 0x{existingHandle:X}");
                SetHandle(existingHandle);
                // System.Console.WriteLine($"[CRT-HANDLE] Handle set successfully");
            }
            
            // Default constructor for new handles
            public Handle() { }

            protected override bool ReleaseHandle()
            {
                API.destroy(handle);
                return true;
            }
        }

        internal Handle NativeHandle { get; private set; }
        private HttpClientConnectionOptions options;
        // Keep track of streams created by this connection until they complete to
        // keep them from being GC'ed
        private HashSet<HttpClientStream> streams = new HashSet<HttpClientStream>();

        private HttpClientConnection(HttpClientConnectionOptions options)
        {
            options.Validate();

            this.options = options;
            NativeHandle = API.make_new(
                options.ClientBootstrap.NativeHandle.DangerousGetHandle(),
                options.InitialWindowSize,
                options.HostName,
                options.Port,
                options.SocketOptions?.NativeHandle.DangerousGetHandle() ?? IntPtr.Zero,
                options.TlsConnectionOptions?.NativeHandle.DangerousGetHandle() ?? IntPtr.Zero,
                OnConnectionSetup,
                OnConnectionShutdown);
        }

        /// <summary>
        /// Internal constructor for creating HttpClientConnection from existing native handle
        /// Used by HttpClientConnectionManager when acquiring pooled connections
        /// </summary>
        internal HttpClientConnection(IntPtr nativeHandle, HttpClientConnectionOptions options)
        {
            // System.Console.WriteLine($"[CRT-CONN-WRAP] Creating HttpClientConnection from existing handle");
            // System.Console.WriteLine($"[CRT-CONN-WRAP] Native handle: 0x{nativeHandle:X}");
            // System.Console.WriteLine($"[CRT-CONN-WRAP] Handle valid: {nativeHandle != IntPtr.Zero}");
            
            this.options = options;
            
            // Properly wrap the existing native handle from the connection pool
            if (nativeHandle != IntPtr.Zero)
            {
                // System.Console.WriteLine($"[CRT-CONN-WRAP] Creating Handle with existing native handle");
                this.NativeHandle = new Handle(nativeHandle);
                // System.Console.WriteLine($"[CRT-CONN-WRAP] Handle wrapped successfully");
            }
            else
            {
                // System.Console.WriteLine($"[CRT-CONN-WRAP] ERROR: Received null native handle");
                throw new ArgumentException("Native handle cannot be null", nameof(nativeHandle));
            }
        }

        private class ConnectionBootstrap
        {
            public CrtResult<HttpClientConnection> Result = new CrtResult<HttpClientConnection>();
            public HttpClientConnection Connection;
        }
        public static CrtResult<HttpClientConnection> NewConnection(HttpClientConnectionOptions options)
        {
            options.Validate();

            var bootstrap = new ConnectionBootstrap();
            options.ConnectionSetup += (sender, e) => {
                if (e.ErrorCode != 0)
                {
                    var message = CRT.ErrorString(e.ErrorCode);
                    bootstrap.Result.CompleteExceptionally(new WebException(String.Format("Failed to connect: {0}", message)));
                }
                else
                {
                    bootstrap.Result.Complete(bootstrap.Connection);
                }
            };

            bootstrap.Connection = new HttpClientConnection(options);
            return bootstrap.Result;
        }

        private class StreamBootstrap
        {
            public CrtResult<StreamResult> Result = new CrtResult<StreamResult>();
            public HttpClientStream Stream;
        }
        public CrtResult<StreamResult> MakeRequest(HttpRequest request, HttpResponseStreamHandler responseHandler)
        {
            // System.Console.WriteLine($"[CRT-CONN] MakeRequest called");
            // System.Console.WriteLine($"[CRT-CONN] Request method: {request.Method}");
            // System.Console.WriteLine($"[CRT-CONN] Request URI: {request.Uri}");
            // System.Console.WriteLine($"[CRT-CONN] Request headers count: {request.Headers?.Length ?? 0}");
            // System.Console.WriteLine($"[CRT-CONN] Native handle valid: {NativeHandle != null && !NativeHandle.IsInvalid}");
            
            if (NativeHandle != null)
            {
                // System.Console.WriteLine($"[CRT-CONN] Native handle address: 0x{NativeHandle.DangerousGetHandle():X}");
            }
            
            var bootstrap = new StreamBootstrap();
            // System.Console.WriteLine($"[CRT-CONN] StreamBootstrap created with Result.Id: {bootstrap.Result.GetHashCode():X8}");
            
            // System.Console.WriteLine($"[CRT-CONN] Adding StreamComplete event handler (THIS IS THE DUAL HANDLER!)");
            responseHandler.StreamComplete += (sender, e) => {
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] *** MakeRequest StreamComplete handler fired ***");
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] Error code: {e.ErrorCode}");
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] Stream: {bootstrap.Stream?.GetHashCode():X8}");
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] Sender: {sender?.GetHashCode():X8}");
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] Current streams count before removal: {streams.Count}");
                
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] About to remove stream from streams collection...");
                var removeResult = streams.Remove(bootstrap.Stream);
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] Stream removal result: {removeResult}");
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] Current streams count after removal: {streams.Count}");
                
                if (e.ErrorCode != 0)
                {
                    // System.Console.WriteLine($"[CRT-CONN-HANDLER] Setting bootstrap result exception for error code: {e.ErrorCode}");
                    var message = CRT.ErrorString(e.ErrorCode);
                    bootstrap.Result.CompleteExceptionally(new WebException($"Stream {bootstrap.Stream} failed: {message}"));
                    // System.Console.WriteLine($"[CRT-CONN-HANDLER] Bootstrap result set to exception");
                }
                else
                {
                    // System.Console.WriteLine($"[CRT-CONN-HANDLER] Setting bootstrap result success");
                    bootstrap.Result.Complete(new StreamResult(e.ErrorCode));
                    // System.Console.WriteLine($"[CRT-CONN-HANDLER] Bootstrap result set to success");
                }
                
                // System.Console.WriteLine($"[CRT-CONN-HANDLER] *** MakeRequest StreamComplete handler completed ***");
            };
            // System.Console.WriteLine($"[CRT-CONN] StreamComplete event handler added");

            // System.Console.WriteLine($"[CRT-CONN] Creating HttpClientStream...");
            try 
            {
                bootstrap.Stream = new HttpClientStream(this, request, responseHandler);
                // System.Console.WriteLine($"[CRT-CONN] HttpClientStream created successfully with ID: {bootstrap.Stream.GetHashCode():X8}");
            }
            catch (Exception ex)
            {
                // System.Console.WriteLine($"[CRT-CONN] ERROR: Failed to create HttpClientStream: {ex.Message}");
                // System.Console.WriteLine($"[CRT-CONN] Exception type: {ex.GetType().Name}");
                // System.Console.WriteLine($"[CRT-CONN] Stack trace: {ex.StackTrace}");
                throw;
            }
            
            streams.Add(bootstrap.Stream);
            // System.Console.WriteLine($"[CRT-CONN] Stream added to streams collection, total count: {streams.Count}");
            
            // System.Console.WriteLine($"[CRT-CONN] Activating stream...");
            bootstrap.Stream.Activate();
            // System.Console.WriteLine($"[CRT-CONN] Stream activated successfully");
            
            // System.Console.WriteLine($"[CRT-CONN] Returning bootstrap.Result with ID: {bootstrap.Result.GetHashCode():X8}");
            return bootstrap.Result;
        }

        private void OnConnectionSetup(int errorCode)
        {
            options.OnConnectionSetup(this, errorCode);
        }

        private void OnConnectionShutdown(int errorCode)
        {
            options.OnConnectionShutdown(this, errorCode);
        }
    }
}
