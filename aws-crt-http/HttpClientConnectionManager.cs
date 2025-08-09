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
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;

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
        }

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

            // Create callback to handle connection acquisition result
            API.OnConnectionAcquired callback = (connectionPtr, errorCode) =>
            {
                if (errorCode != 0)
                {
                    var message = CRT.ErrorString(errorCode);
                    bootstrap.Result.CompleteExceptionally(new WebException(string.Format("Failed to acquire connection: {0}", message)));
                }
                else if (connectionPtr == IntPtr.Zero)
                {
                    bootstrap.Result.CompleteExceptionally(new WebException("Failed to acquire connection: null connection returned"));
                }
                else
                {
                    // Create HttpClientConnection wrapper around the native connection
                    var connection = CreateConnectionFromHandle(connectionPtr);
                    bootstrap.Result.Complete(connection);
                }
            };

            // Request connection from native connection manager
            API.acquire_connection(NativeHandle.DangerousGetHandle(), callback);

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
            
            pooledOptions.ConnectionShutdown += (sender, e) => { /* handler */ };


            return new HttpClientConnection(connectionHandle, pooledOptions);
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
