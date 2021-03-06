﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices
{
    using Microsoft.Azure.Devices.Common;
    using Microsoft.Azure.Devices.Common.Exceptions;
    using Microsoft.Azure.Devices.Common.Extensions;
    using Microsoft.Azure.Devices.Shared;
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;
    using System.Threading.Tasks;
#if NET451
    using System.Net.Http.Formatting;
#endif

    sealed class HttpClientHelper : IHttpClientHelper
    {
#if !NETSTANDARD1_3 && !NETSTANDARD2_0
        static readonly JsonMediaTypeFormatter JsonFormatter = new JsonMediaTypeFormatter();
#endif
        readonly Uri baseAddress;
        readonly IAuthorizationHeaderProvider authenticationHeaderProvider;
        readonly IReadOnlyDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> defaultErrorMapping;
        HttpClient httpClientObj;
        HttpClient httpClientObjWithPerRequestTimeout;
        bool isDisposed;
        readonly TimeSpan defaultOperationTimeout;

        public HttpClientHelper(
            Uri baseAddress,
            IAuthorizationHeaderProvider authenticationHeaderProvider,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> defaultErrorMapping,
            TimeSpan timeout,
            Action<HttpClient> preRequestActionForAllRequests,
            IWebProxy customHttpProxy)
        {
            this.baseAddress = baseAddress;
            this.authenticationHeaderProvider = authenticationHeaderProvider;
            this.defaultErrorMapping =
                new ReadOnlyDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>(defaultErrorMapping);
            this.defaultOperationTimeout = timeout;

            if (customHttpProxy != DefaultWebProxySettings.Instance)
            {
                HttpClientHandler httpClientHandler = new HttpClientHandler();
                httpClientHandler.UseProxy = (customHttpProxy != null);
                httpClientHandler.Proxy = customHttpProxy;
                this.httpClientObj = new HttpClient(httpClientHandler);
            }
            else
            {
                this.httpClientObj = new HttpClient();
            }

            this.httpClientObj.BaseAddress = this.baseAddress;
            this.httpClientObj.Timeout = timeout;
            this.httpClientObj.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(CommonConstants.MediaTypeForDeviceManagementApis));
            this.httpClientObj.DefaultRequestHeaders.ExpectContinue = false;

            this.httpClientObjWithPerRequestTimeout = new HttpClient();
            this.httpClientObjWithPerRequestTimeout.BaseAddress = this.baseAddress;
            this.httpClientObjWithPerRequestTimeout.Timeout = Timeout.InfiniteTimeSpan;
            this.httpClientObjWithPerRequestTimeout.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(CommonConstants.MediaTypeForDeviceManagementApis));
            this.httpClientObjWithPerRequestTimeout.DefaultRequestHeaders.ExpectContinue = false;

            if (preRequestActionForAllRequests != null)
            {
                preRequestActionForAllRequests(this.httpClientObj);
                preRequestActionForAllRequests(this.httpClientObjWithPerRequestTimeout);
            }
        }

        public Task<T> GetAsync<T>(
            Uri requestUri,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            CancellationToken cancellationToken)
        {
            return this.GetAsync<T>(requestUri, this.defaultOperationTimeout, errorMappingOverrides, customHeaders, true, cancellationToken);
        }

        public Task<T> GetAsync<T>(
            Uri requestUri,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            bool throwIfNotFound,
            CancellationToken cancellationToken)
        {
            return this.GetAsync<T>(requestUri, this.defaultOperationTimeout, errorMappingOverrides, customHeaders, throwIfNotFound, cancellationToken);
        }

        public async Task<T> GetAsync<T>(
            Uri requestUri,
            TimeSpan operationTimeout,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            bool throwIfNotFound,
            CancellationToken cancellationToken)
        {
            T result = default(T);

            if (operationTimeout != this.defaultOperationTimeout && operationTimeout > TimeSpan.Zero)
            {
                if (throwIfNotFound)
                {
                    await this.ExecuteWithOperationTimeoutAsync(
                        HttpMethod.Get,
                        new Uri(this.baseAddress, requestUri),
                        operationTimeout,
                        (requestMsg, token) => AddCustomHeaders(requestMsg, customHeaders),
                        IsMappedToException,
                        async (message, token) => result = await ReadResponseMessageAsync<T>(message, token).ConfigureAwait(false),
                        errorMappingOverrides,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await this.ExecuteWithOperationTimeoutAsync(
                       HttpMethod.Get,
                       new Uri(this.baseAddress, requestUri),
                        operationTimeout,
                       (requestMsg, token) => AddCustomHeaders(requestMsg, customHeaders),
                       message => !(message.IsSuccessStatusCode || message.StatusCode == HttpStatusCode.NotFound),
                       async (message, token) => result = message.StatusCode == HttpStatusCode.NotFound ? (default(T)) : await ReadResponseMessageAsync<T>(message, token).ConfigureAwait(false),
                       errorMappingOverrides,
                       cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (throwIfNotFound)
                {
                    await this.ExecuteAsync(
                        HttpMethod.Get,
                        new Uri(this.baseAddress, requestUri),
                        (requestMsg, token) => AddCustomHeaders(requestMsg, customHeaders),
                        async (message, token) => result = await ReadResponseMessageAsync<T>(message, token).ConfigureAwait(false),
                        errorMappingOverrides,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await this.ExecuteAsync(
                       this.httpClientObj,
                       HttpMethod.Get,
                       new Uri(this.baseAddress, requestUri),
                       (requestMsg, token) => AddCustomHeaders(requestMsg, customHeaders),
                       message => !(message.IsSuccessStatusCode || message.StatusCode == HttpStatusCode.NotFound),
                       async (message, token) => result = message.StatusCode == HttpStatusCode.NotFound ? (default(T)) : await ReadResponseMessageAsync<T>(message, token).ConfigureAwait(false),
                       errorMappingOverrides,
                       cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }

        public async Task<T> PutAsync<T>(
            Uri requestUri,
            T entity,
            PutOperationType operationType,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken) where T : IETagHolder
        {
            T result = default(T);

            await this.ExecuteAsync(
                    HttpMethod.Put,
                    new Uri(this.baseAddress, requestUri),
                    (requestMsg, token) =>
                    {
                        InsertEtag(requestMsg, entity, operationType);
#if NETSTANDARD1_3 || NETSTANDARD2_0
                        var str = Newtonsoft.Json.JsonConvert.SerializeObject(entity);
                        requestMsg.Content = new StringContent(str, System.Text.Encoding.UTF8, "application/json");
#else
                        requestMsg.Content = new ObjectContent<T>(entity, JsonFormatter);
#endif
                        return Task.FromResult(0);
                    },
                    async (httpClient, token) => result = await ReadResponseMessageAsync<T>(httpClient, token).ConfigureAwait(false),
                    errorMappingOverrides,
                    cancellationToken).ConfigureAwait(false);

            return result;
        }

        public async Task<T2> PutAsync<T, T2>(
            Uri requestUri,
            T entity,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken)
        {
            T2 result = default(T2);

            await this.ExecuteAsync(
                    HttpMethod.Put,
                    new Uri(this.baseAddress, requestUri),
                    (requestMsg, token) =>
                    {
#if NETSTANDARD1_3 || NETSTANDARD2_0
                        var str = Newtonsoft.Json.JsonConvert.SerializeObject(entity);
                        requestMsg.Content = new StringContent(str, System.Text.Encoding.UTF8, "application/json");
#else
                        requestMsg.Content = new ObjectContent<T>(entity, JsonFormatter);
#endif
                        return Task.FromResult(0);
                    },
                    async (httpClient, token) => result = await ReadResponseMessageAsync<T2>(httpClient, token).ConfigureAwait(false),
                    errorMappingOverrides,
                    cancellationToken).ConfigureAwait(false);

            return result;
        }

        public async Task PutAsync<T>(
            Uri requestUri,
            T entity,
            string etag,
            PutOperationType operationType,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken)
        {
            await this.ExecuteAsync(
                HttpMethod.Put,
                new Uri(this.baseAddress, requestUri),
                (requestMsg, token) =>
                {
                    InsertEtag(requestMsg, etag, operationType);
#if NETSTANDARD1_3 || NETSTANDARD2_0
                    var str = Newtonsoft.Json.JsonConvert.SerializeObject(entity);
                    requestMsg.Content = new StringContent(str, System.Text.Encoding.UTF8, "application/json");
#else
                    requestMsg.Content = new ObjectContent<T>(entity, JsonFormatter);
#endif
                    return Task.FromResult(0);
                },
                null,
                errorMappingOverrides,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<T2> PutAsync<T, T2>(
            Uri requestUri,
            T entity,
            string etag,
            PutOperationType operationType,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken)
        {
            T2 result = default(T2);

            await this.ExecuteAsync(
                HttpMethod.Put,
                new Uri(this.baseAddress, requestUri),
                (requestMsg, token) =>
                {
                    // TODO: skintali: Use string etag when service side changes are ready
                    InsertEtag(requestMsg, etag, operationType);
#if NETSTANDARD1_3 || NETSTANDARD2_0
                    var str = Newtonsoft.Json.JsonConvert.SerializeObject(entity);
                    requestMsg.Content = new StringContent(str, System.Text.Encoding.UTF8, "application/json");
#else
                    requestMsg.Content = new ObjectContent<T>(entity, JsonFormatter);
#endif
                    return Task.FromResult(0);
                },
                async (httpClient, token) => result = await ReadResponseMessageAsync<T2>(httpClient, token).ConfigureAwait(false),
                errorMappingOverrides,
                cancellationToken).ConfigureAwait(false);

            return result;
        }

        public async Task PatchAsync<T>(Uri requestUri, T entity, string etag,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides, CancellationToken cancellationToken)
        {
            await this.ExecuteAsync(
                new HttpMethod("PATCH"),
                new Uri(this.baseAddress, requestUri),
                (requestMsg, token) =>
                {
                    InsertEtag(requestMsg, etag, PutOperationType.UpdateEntity);
#if NETSTANDARD1_3 || NETSTANDARD2_0
                    var str = Newtonsoft.Json.JsonConvert.SerializeObject(entity);
                    requestMsg.Content = new StringContent(str, System.Text.Encoding.UTF8, "application/json");
#else
                    requestMsg.Content = new ObjectContent<T>(entity, JsonFormatter);
#endif
                    return Task.FromResult(0);
                },
                null,
                errorMappingOverrides,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<T2> PatchAsync<T, T2>(Uri requestUri, T entity, string etag,
            PutOperationType putOperationType,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken)
        {
            T2 result = default(T2);

            await this.ExecuteAsync(
                new HttpMethod("PATCH"),
                new Uri(this.baseAddress, requestUri),
                (requestMsg, token) =>
                {
                    InsertEtag(requestMsg, etag, putOperationType);
#if NETSTANDARD1_3 || NETSTANDARD2_0
                    var str = Newtonsoft.Json.JsonConvert.SerializeObject(entity);
                    requestMsg.Content = new StringContent(str, System.Text.Encoding.UTF8, "application/json");
#else
                    requestMsg.Content = new ObjectContent<T>(entity, JsonFormatter);
#endif
                    return Task.FromResult(0);
                },
                async (httpClient, token) => result = await ReadResponseMessageAsync<T2>(httpClient, token).ConfigureAwait(false),
                errorMappingOverrides,
                cancellationToken).ConfigureAwait(false);

            return result;
        }

        static async Task<T> ReadResponseMessageAsync<T>(HttpResponseMessage message, CancellationToken token)
        {
            if (typeof(T) == typeof(HttpResponseMessage))
            {
                return (T)(object)message;
            }

#if NETSTANDARD1_3 || NETSTANDARD2_0
            var str = await message.Content.ReadAsStringAsync().ConfigureAwait(false);
            T entity = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(str);
#else
            T entity = await message.Content.ReadAsAsync<T>(token).ConfigureAwait(false);
#endif
            // Etag in the header is considered authoritative
            var eTagHolder = entity as IETagHolder;
            if (eTagHolder != null)
            {
                if (message.Headers.ETag != null && !string.IsNullOrWhiteSpace(message.Headers.ETag.Tag))
                {
                    // RDBug 3429280:Make the version field of Device object internal
                    eTagHolder.ETag = message.Headers.ETag.Tag;
                }
            }

            return entity;
        }

        static Task AddCustomHeaders(HttpRequestMessage requestMessage, IDictionary<string, string> customHeaders)
        {
            if (customHeaders != null)
            {
                foreach (var header in customHeaders)
                {
                    requestMessage.Headers.Add(header.Key, header.Value);
                }
            }

            return Task.FromResult(0);
        }

        static void InsertEtag(HttpRequestMessage requestMessage, IETagHolder entity, PutOperationType operationType)
        {
            if (operationType == PutOperationType.CreateEntity)
            {
                return;
            }

            if (operationType == PutOperationType.ForceUpdateEntity)
            {
                const string etag = "\"*\"";
                requestMessage.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
            }
            else
            {
                InsertEtag(requestMessage, entity.ETag);
            }
        }

        static void InsertEtag(HttpRequestMessage requestMessage, string etag, PutOperationType operationType)
        {
            if (operationType == PutOperationType.CreateEntity)
            {
                return;
            }

            string etagString = "\"*\"";
            if (operationType == PutOperationType.ForceUpdateEntity)
            {
                requestMessage.Headers.IfMatch.Add(new EntityTagHeaderValue(etagString));
            }
            else
            {
                InsertEtag(requestMessage, etag);
            }
        }

        static void InsertEtag(HttpRequestMessage requestMessage, string etag)
        {
            if (string.IsNullOrWhiteSpace(etag))
            {
                throw new ArgumentException("The entity does not have its ETag set.");
            }

            if (!etag.StartsWith("\"", StringComparison.OrdinalIgnoreCase))
            {
                etag = "\"" + etag;
            }

            if (!etag.EndsWith("\"", StringComparison.OrdinalIgnoreCase))
            {
                etag = etag + "\"";
            }

            requestMessage.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        }

        IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> MergeErrorMapping(
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides)
        {
            var mergedMapping = this.defaultErrorMapping.ToDictionary(mapping => mapping.Key, mapping => mapping.Value);

            if (errorMappingOverrides != null)
            {
                foreach (var @override in errorMappingOverrides)
                {
                    mergedMapping[@override.Key] = @override.Value;
                }
            }

            return mergedMapping;
        }

        public Task PostAsync<T>(
            Uri requestUri,
            T entity,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            CancellationToken cancellationToken)
        {
            return this.PostAsyncHelper(
                requestUri,
                entity,
                TimeSpan.Zero,
                errorMappingOverrides,
                customHeaders,
                null,
                null,
                ReadResponseMessageAsync<HttpResponseMessage>,
                cancellationToken);
        }

        public Task PostAsync<T>(
            Uri requestUri,
            T entity,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            TimeSpan operationTimeout,
            CancellationToken cancellationToken)
        {
            return this.PostAsyncHelper(
                requestUri,
                entity,
                operationTimeout,
                errorMappingOverrides,
                customHeaders,
                null,
                null,
                ReadResponseMessageAsync<HttpResponseMessage>,
                cancellationToken);
        }

        public async Task<T2> PostAsync<T1, T2>(
            Uri requestUri,
            T1 entity,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            CancellationToken cancellationToken)
        {
            T2 result = default(T2);
            await this.PostAsyncHelper(
                requestUri,
                entity,
                TimeSpan.Zero,
                errorMappingOverrides,
                customHeaders,
                null,
                null,
                async (message, token) => result = await ReadResponseMessageAsync<T2>(message, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            return result;
        }

        public async Task<T2> PostAsync<T, T2>(
            Uri requestUri,
            T entity,
            TimeSpan operationTimeout,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            CancellationToken cancellationToken)
        {
            T2 result = default(T2);
            await this.PostAsyncHelper(
                requestUri,
                entity,
                operationTimeout,
                errorMappingOverrides,
                customHeaders,
                null,
                null,
                async (message, token) => result = await ReadResponseMessageAsync<T2>(message, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            return result;
        }

        public async Task<T2> PostAsync<T, T2>(
            Uri requestUri,
            T entity,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            MediaTypeHeaderValue customContentType,
            ICollection<string> customContentEncoding,
            CancellationToken cancellationToken)
        {
            T2 result = default(T2);
            await this.PostAsyncHelper(
                requestUri,
                entity,
                TimeSpan.Zero,
                errorMappingOverrides,
                customHeaders,
                customContentType,
                customContentEncoding,
                async (message, token) => result = await ReadResponseMessageAsync<T2>(message, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            return result;
        }

        public async Task<HttpResponseMessage> PostAsync<T>(
            Uri requestUri,
            T entity, IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            MediaTypeHeaderValue customContentType,
            ICollection<string> customContentEncoding,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage result = default(HttpResponseMessage);
            await this.PostAsyncHelper(
                requestUri,
                entity,
                TimeSpan.Zero,
                errorMappingOverrides,
                customHeaders,
                customContentType,
                customContentEncoding,
                async (message, token) => result = message,
                cancellationToken).ConfigureAwait(false);
            return result;
        }

        Task PostAsyncHelper<T1>(
            Uri requestUri,
            T1 entity,
            TimeSpan operationTimeout,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            MediaTypeHeaderValue customContentType,
            ICollection<string> customContentEncoding,
            Func<HttpResponseMessage, CancellationToken, Task> processResponseMessageAsync,
            CancellationToken cancellationToken)
        {
            Func<HttpRequestMessage, CancellationToken, Task> modifyRequestMessageFunc = (requestMsg, token) =>
            {
                AddCustomHeaders(requestMsg, customHeaders);
                if (entity != null)
                {
                    if (typeof(T1) == typeof(byte[]))
                    {
                        requestMsg.Content = new ByteArrayContent((byte[])(object)entity);
                    }
                    else if (typeof(T1) == typeof(string))
                    {
                        // only used to send batched messages on Http runtime
                        requestMsg.Content = new StringContent((string)(object)entity);
                        requestMsg.Content.Headers.ContentType = new MediaTypeHeaderValue(CommonConstants.BatchedMessageContentType);
                    }
                    else
                    {
                        var str = Newtonsoft.Json.JsonConvert.SerializeObject(entity);
                        requestMsg.Content = new StringContent(str, System.Text.Encoding.UTF8, "application/json");
                    }
                }

                if (customContentType != null)
                {
                    requestMsg.Content.Headers.ContentType = customContentType;
                }

                if (customContentEncoding != null && customContentEncoding.Count > 0)
                {
                    foreach (string contentEncoding in customContentEncoding)
                    {
                        requestMsg.Content.Headers.ContentEncoding.Add(contentEncoding);
                    }
                }

                return Task.FromResult(0);
            };

            if (operationTimeout != this.defaultOperationTimeout && operationTimeout > TimeSpan.Zero)
            {
                return this.ExecuteWithOperationTimeoutAsync(
                    HttpMethod.Post,
                    new Uri(this.baseAddress, requestUri),
                    operationTimeout,
                    modifyRequestMessageFunc,
                    IsMappedToException,
                    processResponseMessageAsync, 
                    errorMappingOverrides,
                    cancellationToken);
            }
            else
            {
                return this.ExecuteAsync(
                    HttpMethod.Post,
                    new Uri(this.baseAddress, requestUri),
                    modifyRequestMessageFunc,
                    processResponseMessageAsync,
                    errorMappingOverrides,
                    cancellationToken);
            }
        }

        public Task DeleteAsync<T>(
            Uri requestUri,
            T entity,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            CancellationToken cancellationToken) where T : IETagHolder
        {
            return this.ExecuteAsync(
                    HttpMethod.Delete,
                    new Uri(this.baseAddress, requestUri),
                    (requestMsg, token) =>
                    {
                        InsertEtag(requestMsg, entity.ETag);
                        AddCustomHeaders(requestMsg, customHeaders);
                        return TaskHelpers.CompletedTask;
                    },
                    null,
                    errorMappingOverrides,
                    cancellationToken);
        }

        public async Task<T> DeleteAsync<T>(
            Uri requestUri,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            IDictionary<string, string> customHeaders,
            CancellationToken cancellationToken)
        {
            T result = default(T);

            await this.ExecuteAsync(
                    HttpMethod.Delete,
                    new Uri(this.baseAddress, requestUri),
                    (requestMsg, token) =>
                    {
                        AddCustomHeaders(requestMsg, customHeaders);
                        return TaskHelpers.CompletedTask;
                    },
                    async (message, token) => result = await ReadResponseMessageAsync<T>(message, token).ConfigureAwait(false),
                    errorMappingOverrides,
                    cancellationToken).ConfigureAwait(false);

            return result;
        }

        Task ExecuteAsync(
            HttpMethod httpMethod,
            Uri requestUri,
            Func<HttpRequestMessage, CancellationToken, Task> modifyRequestMessageAsync,
            Func<HttpResponseMessage, CancellationToken, Task> processResponseMessageAsync,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken)
        {
            return this.ExecuteAsync(
                this.httpClientObj,
                httpMethod,
                requestUri,
                modifyRequestMessageAsync,
                IsMappedToException,
                processResponseMessageAsync,
                errorMappingOverrides,
                cancellationToken);
        }

        Task ExecuteWithOperationTimeoutAsync(
            HttpMethod httpMethod,
            Uri requestUri,
            TimeSpan operationTimeout,
            Func<HttpRequestMessage, CancellationToken, Task> modifyRequestMessageAsync,
            Func<HttpResponseMessage, bool> isMappedToException,
            Func<HttpResponseMessage, CancellationToken, Task> processResponseMessageAsync,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken)
        {
            var cts = new CancellationTokenSource(operationTimeout);
            CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
            return this.ExecuteAsync(
                this.httpClientObjWithPerRequestTimeout,
                httpMethod,
                requestUri,
                modifyRequestMessageAsync,
                IsMappedToException,
                processResponseMessageAsync,
                errorMappingOverrides,
                linkedCts.Token);
        }

        public static bool IsMappedToException(HttpResponseMessage message)
        {
            bool isMappedToException = !message.IsSuccessStatusCode;

            // Get any IotHubErrorCode information from the header for special case exemption of exception throwing
            string iotHubErrorCodeAsString = message.Headers.GetFirstValueOrNull(CommonConstants.IotHubErrorCode);
            ErrorCode iotHubErrorCode;
            if (Enum.TryParse(iotHubErrorCodeAsString, out iotHubErrorCode))
            {
                switch (iotHubErrorCode)
                {
                    case ErrorCode.BulkRegistryOperationFailure:
                        isMappedToException = false;
                        break;
                }
            }

            return isMappedToException;
        }

        async Task ExecuteAsync(
            HttpClient httpClient,
            HttpMethod httpMethod,
            Uri requestUri,
            Func<HttpRequestMessage, CancellationToken, Task> modifyRequestMessageAsync,
            Func<HttpResponseMessage, bool> isMappedToException,
            Func<HttpResponseMessage, CancellationToken, Task> processResponseMessageAsync,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMappingOverrides,
            CancellationToken cancellationToken)
        {
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> mergedErrorMapping =
                this.MergeErrorMapping(errorMappingOverrides);

            using (var msg = new HttpRequestMessage(httpMethod, requestUri))
            {
                msg.Headers.Add(HttpRequestHeader.Authorization.ToString(), this.authenticationHeaderProvider.GetAuthorizationHeader());
                msg.Headers.Add(HttpRequestHeader.UserAgent.ToString(), Utils.GetClientVersion());

                if (modifyRequestMessageAsync != null) await modifyRequestMessageAsync(msg, cancellationToken).ConfigureAwait(false);

                // TODO: pradeepc - find out the list of exceptions that HttpClient can throw.
                HttpResponseMessage responseMsg;
                try
                {
                    responseMsg = await httpClient.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                    if (responseMsg == null)
                    {
                        throw new InvalidOperationException("The response message was null when executing operation {0}.".FormatInvariant(httpMethod));
                    }

                    if (!isMappedToException(responseMsg))
                    {
                        if (processResponseMessageAsync != null)
                        {
                            await processResponseMessageAsync(responseMsg, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (AggregateException ex)
                {
                    var innerExceptions = ex.Flatten().InnerExceptions;
                    if (innerExceptions.Any(Fx.IsFatal))
                    {
                        throw;
                    }

                    // Apparently HttpClient throws AggregateException when a timeout occurs.
                    // TODO: pradeepc - need to confirm this with ASP.NET team
                    if (innerExceptions.Any(e => e is TimeoutException))
                    {
                        throw new IotHubCommunicationException(ex.Message, ex);
                    }

                    throw new IotHubException(ex.Message, ex);
                }
                catch (TimeoutException ex)
                {
                    throw new IotHubCommunicationException(ex.Message, ex);
                }
                catch (IOException ex)
                {
                    throw new IotHubCommunicationException(ex.Message, ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new IotHubCommunicationException(ex.Message, ex);
                }
                catch (TaskCanceledException ex)
                {
                    // Unfortunately TaskCanceledException is thrown when HttpClient times out.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new IotHubException(ex.Message, ex);
                    }

                    throw new IotHubCommunicationException(string.Format(CultureInfo.InvariantCulture, "The {0} operation timed out.", httpMethod), ex);
                }
                catch (Exception ex)
                {
                    if (Fx.IsFatal(ex)) throw;

                    throw new IotHubException(ex.Message, ex);
                }

                if (isMappedToException(responseMsg))
                {
                    Exception mappedEx = await MapToExceptionAsync(responseMsg, mergedErrorMapping).ConfigureAwait(false);
                    throw mappedEx;
                }
            }
        }

        static async Task<Exception> MapToExceptionAsync(
            HttpResponseMessage response,
            IDictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>> errorMapping)
        {
            Func<HttpResponseMessage, Task<Exception>> func;
            if (!errorMapping.TryGetValue(response.StatusCode, out func))
            {
                return new IotHubException(
                    await ExceptionHandlingHelper.GetExceptionMessageAsync(response).ConfigureAwait(false),
                    isTransient: true);
            }

            var mapToExceptionFunc = errorMapping[response.StatusCode];
            var exception = mapToExceptionFunc(response);
            return await exception.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (!this.isDisposed)
            {
                this.httpClientObj?.Dispose();
                this.httpClientObjWithPerRequestTimeout?.Dispose();

                this.httpClientObj = null;
                this.httpClientObjWithPerRequestTimeout = null;
            }

            this.isDisposed = true;
        }
    }
}