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

#include "crt.h"
#include "exports.h"

#include <aws/http/connection_manager.h>

struct aws_dotnet_http_client_connection_manager {
    struct aws_http_connection_manager *manager;
};

static void s_destroy_connection_manager_wrapper(struct aws_dotnet_http_client_connection_manager *wrapper) {
    if (wrapper == NULL) {
        return;
    }

    aws_http_connection_manager_release(wrapper->manager);

    aws_mem_release(aws_dotnet_get_allocator(), wrapper);
}

AWS_DOTNET_API struct aws_dotnet_http_client_connection_manager *aws_dotnet_http_client_connection_manager_new(
    struct aws_client_bootstrap *client_bootstrap,
    const char *host_name,
    uint16_t port,
    struct aws_socket_options *socket_options,
    struct aws_tls_connection_options *tls_connection_options,
    int32_t max_connections,
    uint64_t initial_window_size) {

    struct aws_allocator *allocator = aws_dotnet_get_allocator();
    struct aws_dotnet_http_client_connection_manager *wrapper =
        aws_mem_calloc(allocator, 1, sizeof(struct aws_dotnet_http_client_connection_manager));
    if (wrapper == NULL) {
        return NULL;
    }

    struct aws_http_connection_manager_options options;
    AWS_ZERO_STRUCT(options);

    options.bootstrap = client_bootstrap;
    options.initial_window_size = (size_t)initial_window_size;
    options.socket_options = socket_options;
    options.tls_connection_options = tls_connection_options;
    options.host = aws_byte_cursor_from_c_str(host_name);
    options.port = port;
    options.max_connections = max_connections;

    wrapper->manager = aws_http_connection_manager_new(allocator, &options);
    if (wrapper->manager == NULL) {
        goto on_error;
    }

    return wrapper;

on_error:

    s_destroy_connection_manager_wrapper(wrapper);

    return NULL;
}

AWS_DOTNET_API void aws_dotnet_http_client_connection_manager_destroy(struct aws_dotnet_http_client_connection_manager *manager) {
    s_destroy_connection_manager_wrapper(manager);
}

struct aws_dotnet_connection_acquisition_data {
    void (*callback)(struct aws_http_connection *connection, int error_code);
};

static void s_on_connection_setup_wrapper(
    struct aws_http_connection *connection,
    int error_code,
    void *user_data) {
    
    struct aws_dotnet_connection_acquisition_data *cb_data = user_data;
    if (cb_data && cb_data->callback) {
        cb_data->callback(connection, error_code);
    }
    aws_mem_release(aws_dotnet_get_allocator(), cb_data);
}

AWS_DOTNET_API void aws_dotnet_http_client_connection_manager_acquire_connection(
    struct aws_dotnet_http_client_connection_manager *manager,
    void (*callback)(struct aws_http_connection *connection, int error_code)) {
    
    if (manager == NULL || manager->manager == NULL) {
        if (callback) {
            callback(NULL, AWS_ERROR_INVALID_ARGUMENT);
        }
        return;
    }

    struct aws_dotnet_connection_acquisition_data *cb_data = 
        aws_mem_calloc(aws_dotnet_get_allocator(), 1, sizeof(struct aws_dotnet_connection_acquisition_data));
    if (cb_data == NULL) {
        if (callback) {
            callback(NULL, aws_last_error());
        }
        return;
    }

    cb_data->callback = callback;

    aws_http_connection_manager_acquire_connection(
        manager->manager,
        s_on_connection_setup_wrapper,
        cb_data);
}

AWS_DOTNET_API void aws_dotnet_http_client_connection_manager_release_connection(
    struct aws_dotnet_http_client_connection_manager *manager,
    struct aws_http_connection *connection) {
    
    if (manager == NULL || manager->manager == NULL || connection == NULL) {
        return;
    }

    aws_http_connection_manager_release_connection(manager->manager, connection);
}
