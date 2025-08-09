/**
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * SPDX-License-Identifier: Apache-2.0.
 */

#include "http_client.h"
#include "crt.h"
#include "exports.h"
#include "stream.h"

#include <aws/common/string.h>
#include <aws/http/connection.h>
#include <aws/http/request_response.h>
#include <aws/http/http.h>
#include <aws/io/socket.h>
#include <aws/io/stream.h>

typedef void(DOTNET_CALL aws_dotnet_http_on_client_connection_setup_fn)(int error_code);

typedef void(DOTNET_CALL aws_dotnet_http_on_client_connection_shutdown_fn)(int error_code);

struct aws_dotnet_http_connection {
    struct aws_http_connection *connection;
    aws_dotnet_http_on_client_connection_setup_fn *on_setup;
    aws_dotnet_http_on_client_connection_shutdown_fn *on_shutdown;
};

static void s_http_connection_on_setup(struct aws_http_connection *connection, int error_code, void *user_data) {
    (void)connection;
    struct aws_dotnet_http_connection *dotnet_connection = user_data;
    dotnet_connection->connection = connection;
    dotnet_connection->on_setup(error_code);
}

static void s_http_connection_on_shutdown(struct aws_http_connection *connection, int error_code, void *user_data) {
    (void)connection;
    struct aws_dotnet_http_connection *dotnet_connection = user_data;
    dotnet_connection->on_shutdown(error_code);
    dotnet_connection->connection = NULL;
}

static struct aws_socket_options s_default_socket_options = {
    .type = AWS_SOCKET_STREAM,
    .domain = AWS_SOCKET_IPV4,
    .connect_timeout_ms = 3000,
};

AWS_DOTNET_API
struct aws_dotnet_http_connection *aws_dotnet_http_connection_new(
    struct aws_client_bootstrap *client_bootstrap,
    uint64_t initial_window_size,
    const char *host_name,
    uint16_t port,
    struct aws_socket_options *socket_options,
    struct aws_tls_connection_options *tls_connection_options,
    aws_dotnet_http_on_client_connection_setup_fn *on_setup,
    aws_dotnet_http_on_client_connection_shutdown_fn *on_shutdown) {

    struct aws_allocator *allocator = aws_dotnet_get_allocator();
    struct aws_dotnet_http_connection *connection =
        aws_mem_calloc(allocator, 1, sizeof(struct aws_dotnet_http_connection));
    if (!connection) {
        aws_dotnet_throw_exception(aws_last_error(), "Unable to allocate new aws_dotnet_http_connection");
        return NULL;
    }

    struct aws_http_client_connection_options options = AWS_HTTP_CLIENT_CONNECTION_OPTIONS_INIT;
    options.allocator = allocator;
    options.bootstrap = client_bootstrap;
    if (initial_window_size != 0) {
        options.initial_window_size = (size_t)initial_window_size;
    }
    options.host_name = aws_byte_cursor_from_c_str(host_name);
    options.port = port;
    if (!socket_options) {
        socket_options = &s_default_socket_options;
    }
    options.socket_options = socket_options;
    options.tls_options = tls_connection_options;
    options.on_setup = s_http_connection_on_setup;
    options.on_shutdown = s_http_connection_on_shutdown;
    options.user_data = connection;

    connection->on_setup = on_setup;
    connection->on_shutdown = on_shutdown;

    if (aws_http_client_connect(&options)) {
        aws_mem_release(allocator, connection);
        aws_dotnet_throw_exception(aws_last_error(), "Unable to initialize new HTTP client connection");
        return NULL;
    }
    return connection;
}

AWS_DOTNET_API
void aws_dotnet_http_connection_destroy(struct aws_dotnet_http_connection *connection) {
    aws_http_connection_close(connection->connection);
    struct aws_allocator *allocator = aws_dotnet_get_allocator();
    aws_mem_release(allocator, connection);
}

typedef void(aws_dotnet_http_on_incoming_headers_fn)(
    int32_t status_code,
    int32_t header_block,
    struct aws_dotnet_http_header headers[],
    uint32_t header_count);
typedef void(aws_dotnet_http_on_incoming_header_block_done_fn)(bool has_body);
typedef void(aws_dotnet_http_on_incoming_body_fn)(uint8_t *data, uint64_t size);
typedef void(aws_dotnet_http_on_stream_complete_fn)(int error_code);

struct aws_dotnet_http_stream {
    struct aws_dotnet_http_connection *connection;
    struct aws_http_stream *stream;
    struct aws_http_message *request;

    aws_dotnet_http_on_incoming_headers_fn *on_incoming_headers;
    aws_dotnet_http_on_incoming_header_block_done_fn *on_incoming_headers_block_done;
    aws_dotnet_http_on_incoming_body_fn *on_incoming_body;
    aws_dotnet_http_on_stream_complete_fn *on_stream_complete;
};

static int s_stream_on_incoming_headers(
    struct aws_http_stream *s,
    enum aws_http_header_block header_block,
    const struct aws_http_header *headers,
    size_t header_count,
    void *user_data) {
    (void)s;

    // printf("DEADLOCK-TRACE-07: NATIVE headers callback firing - Block: %d, Count: %zu\n", header_block, header_count);

    struct aws_dotnet_http_stream *stream = user_data;
    if (!stream) {
        // printf("DEADLOCK-TRACE-ERROR: stream user_data is NULL!\n");
        return AWS_OP_ERR;
    }
    
    if (!stream->on_incoming_headers) {
        // printf("DEADLOCK-TRACE-ERROR: on_incoming_headers callback is NULL!\n");
        return AWS_OP_ERR;
    }

    AWS_VARIABLE_LENGTH_ARRAY(struct aws_dotnet_http_header, dotnet_headers, header_count);

    for (size_t header_idx = 0; header_idx < header_count; ++header_idx) {
        dotnet_headers[header_idx].name = (const char *)headers[header_idx].name.ptr;
        dotnet_headers[header_idx].name_size = headers[header_idx].name.len;
        dotnet_headers[header_idx].value = (const char *)headers[header_idx].value.ptr;
        dotnet_headers[header_idx].value_size = headers[header_idx].value.len;
    }

    int status = 0;
    aws_http_stream_get_incoming_response_status(stream->stream, &status);
    
    stream->on_incoming_headers(status, header_block, dotnet_headers, (uint32_t)header_count);
    
    // printf("DEADLOCK-TRACE-07: NATIVE headers callback completed successfully\n");
    return AWS_OP_SUCCESS;
}

static int s_stream_on_incoming_header_block_done(
    struct aws_http_stream *s,
    enum aws_http_header_block header_block,
    void *user_data) {
    (void)s;
    struct aws_dotnet_http_stream *stream = user_data;
    if (stream->on_incoming_headers_block_done) {
        stream->on_incoming_headers_block_done(header_block);
    }

    return AWS_OP_SUCCESS;
}

static int s_stream_on_incoming_body(struct aws_http_stream *s, const struct aws_byte_cursor *data, void *user_data) {
    // printf("DEADLOCK-TRACE-08: NATIVE body callback firing - Data: %zu bytes\n", data ? data->len : 0);
    
    struct aws_dotnet_http_stream *stream = user_data;
    if (!stream) {
        // printf("DEADLOCK-TRACE-ERROR: stream user_data is NULL!\n");
        return AWS_OP_ERR;
    }
    
    if (stream->on_incoming_body) {
        stream->on_incoming_body(data->ptr, (uint64_t)data->len);
    }

    // CRITICAL FIX: Update stream window after processing each chunk
    // This is required for proper HTTP flow control and stream completion
    if (data && data->len > 0) {
        aws_http_stream_update_window(s, data->len);
    }

    // printf("DEADLOCK-TRACE-08: NATIVE body callback completed successfully\n");
    return AWS_OP_SUCCESS;
}

static void s_stream_on_stream_complete(struct aws_http_stream *s, int error_code, void *user_data) {
    // printf("DEADLOCK-TRACE-09: NATIVE completion callback firing - Error: %d\n", error_code);
    
    struct aws_dotnet_http_stream *stream = user_data;
    if (!stream) {
        // printf("DEADLOCK-TRACE-ERROR: stream user_data is NULL!\n");
        aws_http_stream_release(s);  // Still release the stream even if user_data is bad
        return;
    }
    
    // CRITICAL FIX: Release stream BEFORE calling .NET to prevent race condition
    // The connection reuse race was caused by releasing after .NET callback
    aws_http_stream_release(s);
    
    if (stream->on_stream_complete) {
        stream->on_stream_complete(error_code);
    }
    
    // printf("DEADLOCK-TRACE-09: NATIVE completion callback completed successfully\n");
}

struct aws_http_message *aws_build_http_request(
    const char *method,
    const char *uri,
    struct aws_dotnet_http_header headers[],
    uint32_t header_count,
    struct aws_dotnet_stream_function_table *body_stream_delegates) {

    // printf("DEADLOCK-TRACE-REQUEST-BODY: Building HTTP request - Method: %s\n", method ? method : "NULL");
    // printf("DEADLOCK-TRACE-REQUEST-BODY: Body stream delegates valid: %s\n", 
        //    aws_stream_function_table_is_valid(body_stream_delegates) ? "YES" : "NO");

    struct aws_allocator *allocator = aws_dotnet_get_allocator();
    struct aws_http_message *request = aws_http_message_new_request(allocator);
    if (request == NULL) {
        // printf("DEADLOCK-TRACE-ERROR: Failed to create HTTP request message\n");
        return NULL;
    }

    if (aws_http_message_set_request_method(request, aws_byte_cursor_from_c_str(method))) {
        // printf("DEADLOCK-TRACE-ERROR: Failed to set request method\n");
        goto on_error;
    }

    if (aws_http_message_set_request_path(request, aws_byte_cursor_from_c_str(uri))) {
        // printf("DEADLOCK-TRACE-ERROR: Failed to set request path\n");
        goto on_error;
    }

    // printf("DEADLOCK-TRACE-REQUEST-BODY: Adding %u headers to request\n", header_count);
    for (size_t i = 0; i < header_count; ++i) {
        struct aws_http_header header;
        AWS_ZERO_STRUCT(header);

        struct aws_string *name_string = aws_string_new_from_c_str(allocator, headers[i].name);
        struct aws_string *value_string = aws_string_new_from_c_str(allocator, headers[i].value);

        header.name = aws_byte_cursor_from_string(name_string);
        header.value = aws_byte_cursor_from_string(value_string);
        if (aws_http_message_add_header(request, header)) {
            // printf("DEADLOCK-TRACE-ERROR: Failed to add header: %s\n", headers[i].name);
            goto on_error;
        }
        
        // Log Content-Length header specifically
        if (strcmp(headers[i].name, "Content-Length") == 0) {
            // printf("DEADLOCK-TRACE-REQUEST-BODY: Content-Length header: %s\n", headers[i].value);
        }
    }

    if (aws_stream_function_table_is_valid(body_stream_delegates)) {
        // printf("DEADLOCK-TRACE-REQUEST-BODY: Creating request body stream from delegates\n");
        
        struct aws_input_stream *body_stream = aws_input_stream_new_dotnet(allocator, body_stream_delegates);
        if (body_stream == NULL) {
            // printf("DEADLOCK-TRACE-ERROR: Failed to create body stream from delegates\n");
            goto on_error;
        }

        // printf("DEADLOCK-TRACE-REQUEST-BODY: Body stream created successfully, attaching to request\n");
        aws_http_message_set_body_stream(request, body_stream);
        /* request takes the ownership */
        aws_input_stream_release(body_stream);
        // printf("DEADLOCK-TRACE-REQUEST-BODY: Body stream attached to request and released\n");
    } else {
        // printf("DEADLOCK-TRACE-REQUEST-BODY: No body stream - request without body\n");
    }

    // printf("DEADLOCK-TRACE-REQUEST-BODY: HTTP request built successfully\n");
    return request;

on_error:
    // printf("DEADLOCK-TRACE-ERROR: HTTP request build failed, cleaning up\n");
    aws_http_message_release(request);
    return NULL;
}

struct aws_http_headers *aws_build_http_headers(struct aws_dotnet_http_header headers[], uint32_t header_count) {

    struct aws_allocator *allocator = aws_dotnet_get_allocator();
    struct aws_http_headers *c_headers = aws_http_headers_new(allocator);
    if (c_headers == NULL) {
        return NULL;
    }

    for (size_t i = 0; i < header_count; ++i) {
        struct aws_http_header header;
        AWS_ZERO_STRUCT(header);

        header.name = aws_byte_cursor_from_c_str(headers[i].name);
        header.value = aws_byte_cursor_from_c_str(headers[i].value);
        if (aws_http_headers_add_header(c_headers, &header)) {
            goto on_error;
        }
    }

    return c_headers;

on_error:

    aws_http_headers_release(c_headers);

    return NULL;
}

static void s_destroy_stream_wrapper(struct aws_dotnet_http_stream *stream_wrapper) {
    if (stream_wrapper == NULL) {
        return;
    }

    if (stream_wrapper->request != NULL) {
        aws_http_message_release(stream_wrapper->request);
    }

    struct aws_allocator *allocator = aws_dotnet_get_allocator();
    // CRITICAL FIX: Don't release stream here - it's released in completion callback
    // aws_http_stream_release(stream_wrapper->stream);  // REMOVED: Causes double-release bug
    aws_mem_release(allocator, stream_wrapper);
}

AWS_DOTNET_API struct aws_dotnet_http_stream *aws_dotnet_http_stream_new(
    struct aws_dotnet_http_connection *connection,
    const char *method,
    const char *uri,
    struct aws_dotnet_http_header headers[],
    uint32_t header_count,
    struct aws_dotnet_stream_function_table body_stream_delegates,
    aws_dotnet_http_on_incoming_headers_fn *on_incoming_headers,
    aws_dotnet_http_on_incoming_header_block_done_fn *on_incoming_headers_block_done,
    aws_dotnet_http_on_incoming_body_fn *on_incoming_body,
    aws_dotnet_http_on_stream_complete_fn *on_stream_complete) {

    if (on_incoming_headers == NULL) {
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "on_incoming_headers must be provided");
        return NULL;
    }

    if (on_stream_complete == NULL) {
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "on_stream_complete must be provided");
        return NULL;
    }

    if (connection == NULL) {
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "connection must be valid");
        return NULL;
    }

    if (connection->connection == NULL) {
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "crt connection must be non-null");
        return NULL;
    }

    struct aws_allocator *allocator = aws_dotnet_get_allocator();

    struct aws_dotnet_http_stream *stream = aws_mem_calloc(allocator, 1, sizeof(struct aws_dotnet_http_stream));
    if (!stream) {
        aws_dotnet_throw_exception(aws_last_error(), "Unable to allocate aws_dotnet_http_stream");
        return NULL;
    }

    stream->connection = connection;
    stream->on_incoming_headers = on_incoming_headers;
    stream->on_incoming_headers_block_done = on_incoming_headers_block_done;
    stream->on_incoming_body = on_incoming_body;
    stream->on_stream_complete = on_stream_complete;

    stream->request = aws_build_http_request(method, uri, headers, header_count, &body_stream_delegates);
    if (stream->request == NULL) {
        goto on_error;
    }

    struct aws_http_make_request_options options;
    AWS_ZERO_STRUCT(options);
    options.self_size = sizeof(struct aws_http_make_request_options);
    options.request = stream->request;
    options.on_response_headers = s_stream_on_incoming_headers;
    options.on_response_header_block_done = s_stream_on_incoming_header_block_done;
    options.on_response_body = s_stream_on_incoming_body;
    options.on_complete = s_stream_on_stream_complete;
    options.user_data = stream;

    stream->stream = aws_http_connection_make_request(connection->connection, &options);
    if (!stream->stream) {
        aws_dotnet_throw_exception(aws_last_error(), "Unable to initialize new aws_http_stream");
        goto on_error;
    }

    return stream;

on_error:

    s_destroy_stream_wrapper(stream);

    return NULL;
}

AWS_DOTNET_API void aws_dotnet_http_stream_destroy(struct aws_dotnet_http_stream *stream) {
    s_destroy_stream_wrapper(stream);
}

AWS_DOTNET_API void aws_dotnet_http_stream_update_window(
    struct aws_dotnet_http_stream *stream,
    uint64_t increment_size) {
    if (!stream) {
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "Invalid HttpStream");
        return;
    }

    aws_http_stream_update_window(stream->stream, (size_t)increment_size);
}

AWS_DOTNET_API void aws_dotnet_http_stream_activate(struct aws_dotnet_http_stream *stream) {
    // printf("DEADLOCK-TRACE-ACTIVATE: Stream activation starting\n");
    
    if (!stream) {
        // printf("DEADLOCK-TRACE-ERROR: Invalid HttpStream - stream is NULL\n");
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "Invalid HttpStream");
        return;
    }

    if (!stream->stream) {
        // printf("DEADLOCK-TRACE-ERROR: Invalid native stream handle - stream->stream is NULL\n");
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "Invalid native stream handle");
        return;
    }

    // printf("DEADLOCK-TRACE-ACTIVATE: Calling aws_http_stream_activate on native stream: %p\n", (void *)stream->stream);
    
    // Check if the stream has a body
    if (stream->request) {
        struct aws_input_stream *body_stream = aws_http_message_get_body_stream(stream->request);
        if (body_stream) {
            // printf("DEADLOCK-TRACE-ACTIVATE: Stream has request body - body_stream: %p\n", (void *)body_stream);
        } else {
            // printf("DEADLOCK-TRACE-ACTIVATE: Stream has NO request body\n");
        }
    } else {
        // printf("DEADLOCK-TRACE-ACTIVATE: No request message available\n");
    }

    aws_http_stream_activate(stream->stream);
    
    // printf("DEADLOCK-TRACE-ACTIVATE: aws_http_stream_activate completed - stream should now be transmitting\n");
}

/* Bridge function for connection manager - accepts raw connection handle */
AWS_DOTNET_API struct aws_dotnet_http_stream *aws_dotnet_http_stream_new_from_manager(
    struct aws_http_connection *raw_connection,
    const char *method,
    const char *uri,
    struct aws_dotnet_http_header headers[],
    uint32_t header_count,
    struct aws_dotnet_stream_function_table body_stream_delegates,
    aws_dotnet_http_on_incoming_headers_fn *on_incoming_headers,
    aws_dotnet_http_on_incoming_header_block_done_fn *on_incoming_headers_block_done,
    aws_dotnet_http_on_incoming_body_fn *on_incoming_body,
    aws_dotnet_http_on_stream_complete_fn *on_stream_complete) {

    // printf("[NATIVE-STREAM-CREATE] *** aws_dotnet_http_stream_new_from_manager called ***\n");
    // printf("[NATIVE-STREAM-CREATE] Raw connection: %p\n", (void *)raw_connection);
    // printf("[NATIVE-STREAM-CREATE] Method: %s\n", method ? method : "NULL");
    // printf("[NATIVE-STREAM-CREATE] URI: %s\n", uri ? uri : "NULL");
    // printf("[NATIVE-STREAM-CREATE] Header count: %u\n", header_count);
    // printf("[NATIVE-STREAM-CREATE] .NET Callback pointers:\n");
    // printf("[NATIVE-STREAM-CREATE]   on_incoming_headers: %p\n", (void *)on_incoming_headers);
    // printf("[NATIVE-STREAM-CREATE]   on_incoming_headers_block_done: %p\n", (void *)on_incoming_headers_block_done);
    // printf("[NATIVE-STREAM-CREATE]   on_incoming_body: %p\n", (void *)on_incoming_body);
    // printf("[NATIVE-STREAM-CREATE]   on_stream_complete: %p\n", (void *)on_stream_complete);

    if (on_incoming_headers == NULL) {
        // printf("[NATIVE-STREAM-CREATE] ERROR: on_incoming_headers is NULL\n");
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "on_incoming_headers must be provided");
        return NULL;
    }

    if (on_stream_complete == NULL) {
        // printf("[NATIVE-STREAM-CREATE] ERROR: on_stream_complete is NULL\n");
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "on_stream_complete must be provided");
        return NULL;
    }

    if (raw_connection == NULL) {
        // printf("[NATIVE-STREAM-CREATE] ERROR: raw_connection is NULL\n");
        aws_dotnet_throw_exception(AWS_ERROR_INVALID_ARGUMENT, "raw_connection must be valid");
        return NULL;
    }

    struct aws_allocator *allocator = aws_dotnet_get_allocator();

    struct aws_dotnet_http_stream *stream = aws_mem_calloc(allocator, 1, sizeof(struct aws_dotnet_http_stream));
    if (!stream) {
        // printf("[NATIVE-STREAM-CREATE] ERROR: Failed to allocate stream wrapper\n");
        aws_dotnet_throw_exception(aws_last_error(), "Unable to allocate aws_dotnet_http_stream");
        return NULL;
    }

    // printf("[NATIVE-STREAM-CREATE] Stream wrapper allocated: %p\n", (void *)stream);

    // No connection wrapper needed - work directly with raw connection
    stream->connection = NULL; // Not using the wrapper
    stream->on_incoming_headers = on_incoming_headers;
    stream->on_incoming_headers_block_done = on_incoming_headers_block_done;
    stream->on_incoming_body = on_incoming_body;
    stream->on_stream_complete = on_stream_complete;

    // printf("[NATIVE-STREAM-CREATE] Callback pointers stored in stream wrapper:\n");
    // printf("[NATIVE-STREAM-CREATE]   stream->on_incoming_headers: %p\n", (void *)stream->on_incoming_headers);
    // printf("[NATIVE-STREAM-CREATE]   stream->on_stream_complete: %p\n", (void *)stream->on_stream_complete);

    stream->request = aws_build_http_request(method, uri, headers, header_count, &body_stream_delegates);
    if (stream->request == NULL) {
        goto on_error;
    }

    struct aws_http_make_request_options options;
    AWS_ZERO_STRUCT(options);
    options.self_size = sizeof(struct aws_http_make_request_options);
    options.request = stream->request;
    options.on_response_headers = s_stream_on_incoming_headers;
    options.on_response_header_block_done = s_stream_on_incoming_header_block_done;
    options.on_response_body = s_stream_on_incoming_body;
    options.on_complete = s_stream_on_stream_complete;
    options.user_data = stream;

    // printf("[NATIVE-STREAM-CREATE] About to call aws_http_connection_make_request...\n");
    // printf("[NATIVE-STREAM-CREATE] Connection state validation:\n");
    
    // CRITICAL FIX: Validate connection state before creating stream
    if (!aws_http_connection_is_open(raw_connection)) {
        // printf("[NATIVE-STREAM-CREATE] ERROR: Connection is not open: %p\n", (void *)raw_connection);
        aws_dotnet_throw_exception(AWS_ERROR_HTTP_CONNECTION_CLOSED, "Connection is closed");
        goto on_error;
    }
    
    // printf("[NATIVE-STREAM-CREATE] Connection is open and valid\n");
    // printf("[NATIVE-STREAM-CREATE] Native callback pointers being registered:\n");
    // printf("[NATIVE-STREAM-CREATE]   options.on_response_headers: %p\n", (void *)options.on_response_headers);
    // printf("[NATIVE-STREAM-CREATE]   options.on_response_body: %p\n", (void *)options.on_response_body);
    // printf("[NATIVE-STREAM-CREATE]   options.on_complete: %p\n", (void *)options.on_complete);
    // printf("[NATIVE-STREAM-CREATE]   options.user_data: %p\n", options.user_data);

    // Use raw connection directly from connection manager
    stream->stream = aws_http_connection_make_request(raw_connection, &options);
    if (!stream->stream) {
        int error_code = aws_last_error();
        // printf("[NATIVE-STREAM-CREATE] ERROR: aws_http_connection_make_request failed with error %d: %s\n", 
            //    error_code, aws_error_debug_str(error_code));
        aws_dotnet_throw_exception(error_code, "Unable to initialize new aws_http_stream from manager connection");
        goto on_error;
    }

    // printf("[NATIVE-STREAM-CREATE] Stream created successfully: %p\n", (void *)stream->stream);
    // printf("[NATIVE-STREAM-CREATE] Returning stream wrapper: %p\n", (void *)stream);
    return stream;

on_error:
    // printf("[NATIVE-STREAM-CREATE] ERROR: Stream creation failed, cleaning up\n");
    s_destroy_stream_wrapper(stream);
    return NULL;
}
