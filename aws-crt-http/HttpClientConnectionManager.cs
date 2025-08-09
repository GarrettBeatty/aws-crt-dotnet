/*
 * Copyright 2010-2019 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

using Aws.Crt;
using Aws.Crt.IO;

namespace Aws.Crt.Http
{
    public sealed class HttpClientConnectionManagerOptions
    {
        public ClientBootstrap Bootstrap;
        public String Host;
        public UInt16 Port;
        public Int32 MaxConnections;
        public UInt64 InitialWindowSize;
        public SocketOptions SocketOptions;
        public TlsConnectionOptions TlsConnectionOptions;
        // TODO: Proxy support
    }

    public sealed class ConnectionAcquisitionEventArgs : EventArgs
    {
        public HttpClientConnection Connection { get; private set; }
        public int ErrorCode { get; private set; }

        internal ConnectionAcquisitionEventArgs(HttpClientConnection connection, int errorCode)
        {
            Connection = connection;
            ErrorCode = errorCode;
        }
    }

    public sealed class HttpClientConnectionManager : IDisposable
    {
        [SecuritySafeCritical]
        internal static class API
        {
            public delegate void OnConnectionAcquired(IntPtr connection, int errorCode);

            static private LibraryHandle library = new LibraryHandle();
            
            public delegate Handle aws_dotnet_http_client_connection_manager_new(
                                    IntPtr clientBootstrap,
                                    [MarshalAs(UnmanagedType.LPStr)] string hostName,
                                    UInt16 port,
                                    IntPtr socketOptions,
                                    IntPtr tlsConnectionOptions,
                                    Int32  maxConnections,
                                    UInt64 initialWindowSize);
                                    
            public delegate void aws_dotnet_http_client_connection_manager_destroy(IntPtr manager);
            
            public delegate void aws_dotnet_http_client_connection_manager_acquire_connection(
                                    IntPtr manager,
                                    OnConnectionAcquired callback);
                                    
            public delegate void aws_dotnet_http_client_connection_manager_release_connection(
                                    IntPtr manager, 
                                    IntPtr connection);

            public static aws_dotnet_http_client_connection_manager_new make_new = NativeAPI.Bind<aws_dotnet_http_client_connection_manager_new>();
            public static aws_dotnet_http_client_connection_manager_destroy destroy = NativeAPI.Bind<aws_dotnet_http_client_connection_manager_destroy>();
            public static aws_dotnet_http_client_connection_manager_acquire_connection acquire_connection = NativeAPI.Bind<aws_dotnet_http_client_connection_manager_acquire_connection>();
            public static aws_dotnet_http_client_connection_manager_release_connection release_connection = NativeAPI.Bind<aws_dotnet_http_client_connection_manager_release_connection>();
        }

        public class Handle : CRT.Handle
        {
            protected override bool ReleaseHandle()
            {
                API.destroy(handle);
                return true;
            }
        }

        internal Handle NativeHandle { get; private set; }
        private HttpClientConnectionManagerOptions options;
        private bool _disposed = false;

        public HttpClientConnectionManager(HttpClientConnectionManagerOptions options) {
            this.options = options;
            NativeHandle = API.make_new(
                options.Bootstrap.NativeHandle.DangerousGetHandle(), 
                options.Host, options.Port, 
                options.SocketOptions?.NativeHandle.DangerousGetHandle() ?? IntPtr.Zero, 
                options.TlsConnectionOptions?.NativeHandle.DangerousGetHandle() ?? IntPtr.Zero, 
                options.MaxConnections, options.InitialWindowSize);
        }

        private class ConnectionAcquisitionBootstrap
        {
            public CrtResult<HttpClientConnection> Result = new CrtResult<HttpClientConnection>();
            public HttpClientConnectionManager Manager;
            public API.OnConnectionAcquired Callback; // Keep callback alive to prevent GC
            public int BootstrapId; // Unique identifier for lifecycle tracking
        }

        // Static collection to keep bootstraps alive until callbacks complete
        private static readonly ConcurrentDictionary<int, ConnectionAcquisitionBootstrap> _activeBootstraps 
            = new ConcurrentDictionary<int, ConnectionAcquisitionBootstrap>();
        private static int _nextBootstrapId = 0;

        /// <summary>
        /// Acquire a connection from the connection pool
        /// </summary>
        /// <returns>A CrtResult that completes with an available HTTP connection</returns>
        public CrtResult<HttpClientConnection> AcquireConnection()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HttpClientConnectionManager));

            var bootstrap = new ConnectionAcquisitionBootstrap();
            bootstrap.Manager = this;
            
            // Assign unique ID and store in static collection to prevent GC
            bootstrap.BootstrapId = Interlocked.Increment(ref _nextBootstrapId);
            
            // System.Console.WriteLine($"[CRT-MGR] Creating bootstrap with ID: {bootstrap.BootstrapId}");

            // Create callback to handle connection acquisition result and store in bootstrap to prevent GC
            bootstrap.Callback = (connectionPtr, errorCode) =>
            {
                // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Connection acquisition callback invoked for bootstrap {bootstrap.BootstrapId}");
                // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Connection pointer: 0x{connectionPtr:X}");
                // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Error code: {errorCode}");
                
                try
                {
                    if (bootstrap == null)
                    {
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] ERROR: bootstrap is null");
                        return;
                    }
                    
                    if (bootstrap.Result == null)
                    {
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] ERROR: bootstrap.Result is null");
                        return;
                    }
                    
                    if (errorCode != 0)
                    {
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Connection acquisition failed with error code: {errorCode}");
                        var message = CRT.ErrorString(errorCode);
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Error message: {message}");
                        bootstrap.Result.CompleteExceptionally(new WebException(string.Format("Failed to acquire connection: {0}", message)));
                    }
                    else if (connectionPtr == IntPtr.Zero)
                    {
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] ERROR: Connection pointer is null");
                        bootstrap.Result.CompleteExceptionally(new WebException("Failed to acquire connection: null connection returned"));
                    }
                    else
                    {
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Creating connection wrapper from handle");
                        // Create HttpClientConnection wrapper around the native connection
                        var connection = CreateConnectionFromHandle(connectionPtr);
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Connection wrapper created, completing result");
                        bootstrap.Result.Complete(connection);
                        // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Result completed successfully");
                    }
                }
                catch (Exception ex)
                {
                    // System.Console.WriteLine($"[CRT-MGR-CALLBACK] EXCEPTION in callback: {ex.Message}");
                    // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Exception type: {ex.GetType().Name}");
                    // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Stack trace: {ex.StackTrace}");
                    
                    if (bootstrap?.Result != null)
                    {
                        bootstrap.Result.CompleteExceptionally(ex);
                    }
                }
                finally
                {
                    // Remove bootstrap from static collection to allow GC
                    // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Removing bootstrap {bootstrap.BootstrapId} from active collection");
                    _activeBootstraps.TryRemove(bootstrap.BootstrapId, out _);
                    // System.Console.WriteLine($"[CRT-MGR-CALLBACK] Active bootstraps count: {_activeBootstraps.Count}");
                }
            };

            // Store bootstrap in static collection BEFORE making native call to prevent GC
            _activeBootstraps[bootstrap.BootstrapId] = bootstrap;
            // System.Console.WriteLine($"[CRT-MGR] Bootstrap {bootstrap.BootstrapId} stored in active collection");
            // System.Console.WriteLine($"[CRT-MGR] Active bootstraps count: {_activeBootstraps.Count}");

            // Request connection from native connection manager
            API.acquire_connection(NativeHandle.DangerousGetHandle(), bootstrap.Callback);

            return bootstrap.Result;
        }

        /// <summary>
        /// Release a connection back to the connection pool
        /// </summary>
        /// <param name="connection">The connection to release</param>
        public void ReleaseConnection(HttpClientConnection connection)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HttpClientConnectionManager));
                
            if (connection?.NativeHandle != null)
            {
                API.release_connection(
                    NativeHandle.DangerousGetHandle(), 
                    connection.NativeHandle.DangerousGetHandle());
            }
        }

        /// <summary>
        /// Create HttpClientConnection wrapper from native handle
        /// Used when acquiring connections from the connection pool
        /// </summary>
        private HttpClientConnection CreateConnectionFromHandle(IntPtr connectionHandle)
        {
            // System.Console.WriteLine($"[CRT-MGR] CreateConnectionFromHandle called");
            // System.Console.WriteLine($"[CRT-MGR] Connection handle: 0x{connectionHandle:X}");
            // System.Console.WriteLine($"[CRT-MGR] Handle valid: {connectionHandle != IntPtr.Zero}");
            
            // Create a basic connection options for the pooled connection
            var pooledOptions = new HttpClientConnectionOptions
            {
                ClientBootstrap = options.Bootstrap,
                InitialWindowSize = (uint)options.InitialWindowSize,
                HostName = options.Host,
                Port = options.Port,
                SocketOptions = options.SocketOptions,
                TlsConnectionOptions = options.TlsConnectionOptions
            };
            
            // System.Console.WriteLine($"[CRT-MGR] Connection options created for {options.Host}:{options.Port}");
            
            pooledOptions.ConnectionShutdown += (sender, e) => { 
                // System.Console.WriteLine($"[CRT-MGR] Connection shutdown event: error code {e.ErrorCode}");
            };

            // System.Console.WriteLine($"[CRT-MGR] Creating HttpClientConnection with internal constructor");
            var connection = new HttpClientConnection(connectionHandle, pooledOptions);
            // System.Console.WriteLine($"[CRT-MGR] HttpClientConnection created successfully");
            
            return connection;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                NativeHandle?.Dispose();
                _disposed = true;
            }
        }
    }
}
