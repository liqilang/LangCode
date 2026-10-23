using System;
using System.Collections;
using LangCode.Domain.Settings;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Workspace
{
    /// <summary>
    /// 可选的向量检索配置：BM25 负责召回，embedding 只用于对候选片段重排。
    /// 未配置 / 调用失败时自动退回纯 BM25F，不影响主流程。
    /// </summary>
    public sealed class EmbeddingOptions
    {
        /// <summary>Off / Ollama / OpenAI Compatible / Local ONNX（大小写不敏感）</summary>
        public string Provider { get; set; } = string.Empty;

        public string Endpoint { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string ApiKey { get; set; } = string.Empty;

        /// <summary>重排权重（0~1）：最终分 = (1-w)*BM25 + w*cosine</summary>
        public double Weight { get; set; } = 0.35;

        /// <summary>本地 ONNX 模型路径（仅 Local ONNX 使用）</summary>
        public string ModelPath { get; set; } = string.Empty;

        /// <summary>WordPiece 词表路径（仅 Local ONNX 使用）</summary>
        public string VocabPath { get; set; } = string.Empty;

        public int MaxTokens { get; set; } = 256;

        /// <summary>
        /// 后台向量索引最多覆盖多少个片段（0 = 只对 BM25 候选做重排）。
        /// 首次启用时会在后台分批建立，避免一次把整仓库都嵌入。
        /// </summary>
        public int MaxChunks { get; set; } = 600;

        public bool IsEnabled =>
            !string.IsNullOrWhiteSpace(Provider) &&
            !string.Equals(Provider.Trim(), "off", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Provider.Trim(), "关闭", StringComparison.OrdinalIgnoreCase);

        public EmbeddingOptions Clone()
        {
            return new EmbeddingOptions
            {
                Provider = Provider,
                Endpoint = Endpoint,
                Model = Model,
                ApiKey = ApiKey,
                Weight = Weight,
                ModelPath = ModelPath,
                VocabPath = VocabPath,
                MaxTokens = MaxTokens,
                MaxChunks = MaxChunks,
            };
        }

        /// <summary>
        /// 从用户设置构造（唯一映射点，界面与 AgentRunner 共用）
        /// </summary>
        public static EmbeddingOptions FromSettings(AppSettings settings)
        {
            if (settings == null)
            {
                return new EmbeddingOptions();
            }

            return new EmbeddingOptions
            {
                Provider = settings.EmbeddingProvider ?? string.Empty,
                Endpoint = settings.EmbeddingEndpoint ?? string.Empty,
                Model = settings.EmbeddingModel ?? string.Empty,
                ApiKey = settings.EmbeddingApiKey ?? string.Empty,
                Weight = Math.Max(0, Math.Min(100, settings.EmbeddingWeightPercent)) / 100.0,
                ModelPath = settings.EmbeddingModelPath ?? string.Empty,
                VocabPath = settings.EmbeddingVocabPath ?? string.Empty,
                MaxTokens = Math.Max(16, Math.Min(512, settings.EmbeddingMaxTokens)),
                MaxChunks = 600,
            };
        }
    }

    /// <summary>
    /// 向量提供商：OpenAI 兼容 / Ollama / 本地 ONNX
    /// </summary>
    public interface IEmbeddingProvider : IDisposable
    {
        string Name { get; }

        bool IsAvailable { get; }

        string LastError { get; }

        Task<IList<float[]>> EmbedAsync(IList<string> inputs, CancellationToken cancellationToken);
    }

    public static class EmbeddingProviderFactory
    {
        public static IEmbeddingProvider Create(EmbeddingOptions options)
        {
            if (options == null || !options.IsEnabled)
            {
                return null;
            }

            var provider = (options.Provider ?? string.Empty).Trim();
            if (provider.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new OllamaEmbeddingProvider(options);
            }

            if (provider.IndexOf("onnx", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new OnnxEmbeddingProvider(options);
            }

            // 其余按 OpenAI 兼容处理（OpenAI / LM Studio / vLLM / TEI / 自建网关）
            return new OpenAiEmbeddingProvider(options);
        }
    }

    // ==================== OpenAI 兼容 ====================

    internal sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

        private readonly EmbeddingOptions _options;

        public OpenAiEmbeddingProvider(EmbeddingOptions options)
        {
            _options = options ?? new EmbeddingOptions();
            LastError = string.Empty;
        }

        public string Name => "OpenAI 兼容";

        public bool IsAvailable => !string.IsNullOrWhiteSpace(ResolveEndpoint()) &&
                                   !string.IsNullOrWhiteSpace(_options.Model);

        public string LastError { get; private set; }

        public async Task<IList<float[]>> EmbedAsync(IList<string> inputs, CancellationToken cancellationToken)
        {
            if (inputs == null || inputs.Count == 0)
            {
                return [];
            }

            var endpoint = ResolveEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("未配置 embedding 端点");
            }

            var results = new List<float[]>(inputs.Count);

            // 多数端点支持批量；每批 32 条，避免单次请求体过大
            const int batchSize = 32;
            for (var offset = 0; offset < inputs.Count; offset += batchSize)
            {
                var batch = inputs.Skip(offset).Take(batchSize).ToList();
                var body = BuildRequestBody(batch);

                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.ApiKey);
                    }

                    using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            LastError = "HTTP " + (int)response.StatusCode + "：" + Trim(text, 300);
                            throw new InvalidOperationException("embedding 请求失败：" + LastError);
                        }

                        var parsed = Deserialize<OpenAiEmbeddingResponse>(text);
                        if (parsed?.Data == null || parsed.Data.Length == 0)
                        {
                            LastError = "响应里没有 data[].embedding";
                            throw new InvalidOperationException(LastError);
                        }

                        var ordered = parsed.Data
                            .OrderBy(item => item.Index)
                            .Select(item => ToVector(item.Embedding))
                            .ToList();

                        // 个别网关不返回 index：按原顺序补齐
                        while (ordered.Count < batch.Count)
                        {
                            ordered.Add([]);
                        }

                        results.AddRange(ordered.Take(batch.Count));
                    }
                }
            }

            LastError = string.Empty;
            return results;
        }

        private string ResolveEndpoint()
        {
            var value = (_options.Endpoint ?? string.Empty).Trim().TrimEnd('/');
            if (value.Length == 0)
            {
                value = "https://api.openai.com/v1";
            }

            return value.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)
                ? value
                : value + "/embeddings";
        }

        private string BuildRequestBody(IList<string> inputs)
        {
            var builder = new StringBuilder();
            builder.Append("{\"model\":").Append(JsonQuote(_options.Model)).Append(",\"input\":[");
            for (var i = 0; i < inputs.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonQuote(Trim(inputs[i], 6000)));
            }

            return builder.Append("]}").ToString();
        }

        private static float[] ToVector(float[] values)
        {
            return values == null ? [] : values.Select(v => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v).ToArray();
        }

        private static T Deserialize<T>(string json)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return (T)serializer.ReadObject(stream);
                }
            }
            catch (Exception)
            {
                return default(T);
            }
        }

        private static string Trim(string text, int max)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private static string JsonQuote(string text)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }

        public void Dispose()
        {
        }

        [DataContract]
        private sealed class OpenAiEmbeddingResponse
        {
            [DataMember(Name = "data")]
            public OpenAiEmbeddingItem[] Data { get; set; }
        }

        [DataContract]
        private sealed class OpenAiEmbeddingItem
        {
            [DataMember(Name = "embedding")]
            public float[] Embedding { get; set; }

            [DataMember(Name = "index")]
            public int Index { get; set; }
        }
    }

    // ==================== Ollama ====================

    internal sealed class OllamaEmbeddingProvider : IEmbeddingProvider
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private readonly EmbeddingOptions _options;

        public OllamaEmbeddingProvider(EmbeddingOptions options)
        {
            _options = options ?? new EmbeddingOptions();
            LastError = string.Empty;
        }

        public string Name => "Ollama";

        public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.Model);

        public string LastError { get; private set; }

        public async Task<IList<float[]>> EmbedAsync(IList<string> inputs, CancellationToken cancellationToken)
        {
            if (inputs == null || inputs.Count == 0)
            {
                return [];
            }

            var endpoint = ResolveEndpoint();
            var results = new List<float[]>(inputs.Count);

            // 新版本 Ollama 支持 /api/embed 批量；老版本（404/405）退回逐条 /api/embeddings。
            // 网络错误 / 超时不再逐条重试，避免 N 个候选各等一次超时
            try
            {
                var batchResults = await EmbedBatchAsync(endpoint, inputs, cancellationToken).ConfigureAwait(false);
                if (batchResults != null && batchResults.Count == inputs.Count)
                {
                    return batchResults;
                }
            }
            catch (OllamaApiNotSupportedException)
            {
                // 端点不支持批量：继续走下面的逐条接口
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw;
            }

            foreach (var input in inputs)
            {
                try
                {
                    results.Add(await EmbedSingleAsync(endpoint, input, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    throw;
                }
            }

            LastError = string.Empty;
            return results;
        }

        private async Task<IList<float[]>> EmbedBatchAsync(string endpoint, IList<string> inputs, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder("{\"model\":").Append(JsonQuote(_options.Model)).Append(",\"input\":[");
            for (var i = 0; i < inputs.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonQuote(Trim(inputs[i], 6000)));
            }

            builder.Append("]}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Content = new StringContent(builder.ToString(), Encoding.UTF8, "application/json");
                AddAuth(request);

                using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var status = (int)response.StatusCode;
                        if (status == 404 || status == 405)
                        {
                            throw new OllamaApiNotSupportedException("HTTP " + status);
                        }

                        throw new InvalidOperationException("HTTP " + status + "：" + Trim(text, 300));
                    }

                    var parsed = Deserialize<OllamaBatchResponse>(text);
                    if (parsed?.Embeddings == null || parsed.Embeddings.Length != inputs.Count)
                    {
                        return null;
                    }

                    return parsed.Embeddings.Select(ToVector).ToList();
                }
            }
        }

        private async Task<float[]> EmbedSingleAsync(string endpoint, string input, CancellationToken cancellationToken)
        {
            var singleEndpoint = endpoint.Replace("/api/embed", "/api/embeddings");
            var body = "{\"model\":" + JsonQuote(_options.Model) + ",\"prompt\":" + JsonQuote(Trim(input, 6000)) + "}";

            using (var request = new HttpRequestMessage(HttpMethod.Post, singleEndpoint))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                AddAuth(request);

                using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("HTTP " + (int)response.StatusCode + "：" + Trim(text, 300));
                    }

                    var parsed = Deserialize<OllamaSingleResponse>(text);
                    return parsed?.Embedding == null ? [] : ToVector(parsed.Embedding);
                }
            }
        }

        private string ResolveEndpoint()
        {
            var value = (_options.Endpoint ?? string.Empty).Trim().TrimEnd('/');
            if (value.Length == 0)
            {
                value = "http://localhost:11434";
            }

            if (value.EndsWith("/api/embed", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith("/api/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return value + "/api/embed";
        }

        private void AddAuth(HttpRequestMessage request)
        {
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.ApiKey);
            }
        }

        private static float[] ToVector(float[] values)
        {
            return values == null ? [] : values.Select(v => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v).ToArray();
        }

        private static T Deserialize<T>(string json)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return (T)serializer.ReadObject(stream);
                }
            }
            catch (Exception)
            {
                return default(T);
            }
        }

        private static string Trim(string text, int max)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private static string JsonQuote(string text)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }

        public void Dispose()
        {
        }

        private sealed class OllamaApiNotSupportedException : Exception
        {
            public OllamaApiNotSupportedException(string message) : base(message)
            {
            }
        }

        [DataContract]
        private sealed class OllamaBatchResponse
        {
            [DataMember(Name = "embeddings")]
            public float[][] Embeddings { get; set; }
        }

        [DataContract]
        private sealed class OllamaSingleResponse
        {
            [DataMember(Name = "embedding")]
            public float[] Embedding { get; set; }
        }
    }

    // ==================== 本地 ONNX（反射加载，未安装则自动禁用） ====================

    /// <summary>
    /// 本地 ONNX embedding：要求系统里已有 <c>Microsoft.ML.OnnxRuntime</c> 程序集，
    /// 并配置 BERT 风格模型（input_ids / attention_mask / token_type_ids）与 vocab.txt。
    /// 任何一步失败都只把 <see cref="IsAvailable"/> 置 false，检索自动回退 BM25。
    /// </summary>
    internal sealed class OnnxEmbeddingProvider : IEmbeddingProvider
    {
        private readonly EmbeddingOptions _options;
        private readonly object _gate = new object();
        private readonly object _runGate = new object();
        private readonly Dictionary<string, int> _vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);

        private object _session;
        private MethodInfo _runMethod;
        private Type _namedValueType;
        private MethodInfo _createTensorMethod;
        private Type _tensorType;
        private string _outputName;
        private string _inputIdsName;
        private string _attentionMaskName;
        private string _tokenTypeIdsName;
        private bool _initialized;

        public OnnxEmbeddingProvider(EmbeddingOptions options)
        {
            _options = options ?? new EmbeddingOptions();
            LastError = string.Empty;
        }

        public string Name => "本地 ONNX";

        public bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _session != null;
            }
        }

        public string LastError { get; private set; }

        public Task<IList<float[]>> EmbedAsync(IList<string> inputs, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                EnsureInitialized();
                if (_session == null)
                {
                    throw new InvalidOperationException(LastError.Length > 0 ? LastError : "本地 ONNX 不可用");
                }

                var results = new List<float[]>(inputs?.Count ?? 0);
                foreach (var input in inputs ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(EmbedOne(input));
                }

                return (IList<float[]>)results;
            }, cancellationToken);
        }

        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (_gate)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;

                try
                {
                    var modelPath = (_options.ModelPath ?? string.Empty).Trim();
                    var vocabPath = (_options.VocabPath ?? string.Empty).Trim();
                    if (!File.Exists(modelPath) || !File.Exists(vocabPath))
                    {
                        LastError = "未配置有效的 ONNX 模型 / 词表路径";
                        return;
                    }

                    LoadVocabulary(vocabPath);

                    var assembly = Assembly.Load("Microsoft.ML.OnnxRuntime");
                    var sessionType = assembly.GetType("Microsoft.ML.OnnxRuntime.InferenceSession");
                    _namedValueType = assembly.GetType("Microsoft.ML.OnnxRuntime.NamedOnnxValue");
                    var denseTensorType = assembly.GetType("Microsoft.ML.OnnxRuntime.Tensors.DenseTensor`1");
                    if (sessionType == null || _namedValueType == null || denseTensorType == null)
                    {
                        LastError = "OnnxRuntime 程序集版本不兼容";
                        return;
                    }

                    _session = Activator.CreateInstance(sessionType, modelPath);
                    _runMethod = sessionType.GetMethods()
                        .FirstOrDefault(method => method.Name == "Run" && method.GetParameters().Length == 1);
                    _createTensorMethod = _namedValueType.GetMethods()
                        .FirstOrDefault(method => method.Name == "CreateFromTensor" && method.IsGenericMethodDefinition);
                    _tensorType = denseTensorType.MakeGenericType(typeof(long));

                    if (_runMethod == null || _createTensorMethod == null)
                    {
                        LastError = "OnnxRuntime 缺少 Run / CreateFromTensor API";
                        _session = null;
                        return;
                    }

                    _inputIdsName = FirstInputName(assembly, "input_ids", 0);
                    _attentionMaskName = FirstInputName(assembly, "attention_mask", 1);
                    _tokenTypeIdsName = FirstInputName(assembly, "token_type_ids", 2);
                    _outputName = FirstOutputName(assembly);
                }
                catch (Exception ex)
                {
                    LastError = "本地 ONNX 初始化失败：" + ex.GetBaseException().Message;
                    _session = null;
                }
            }
        }

        private static IEnumerable<KeyValuePair<string, object>> MetadataOf(object session, string property)
        {
            var value = session.GetType().GetProperty(property)?.GetValue(session);
            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    var key = item.GetType().GetProperty("Key")?.GetValue(item) as string;
                    if (key != null)
                    {
                        yield return new KeyValuePair<string, object>(key, item);
                    }
                }
            }
        }

        private string FirstInputName(Assembly assembly, string expected, int fallbackIndex)
        {
            var names = MetadataOf(_session, "InputMetadata").Select(pair => pair.Key).ToList();
            var match = names.FirstOrDefault(name => name.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null)
            {
                return match;
            }

            return names.Count > fallbackIndex ? names[fallbackIndex] : null;
        }

        private string FirstOutputName(Assembly assembly)
        {
            return MetadataOf(_session, "OutputMetadata").Select(pair => pair.Key).FirstOrDefault();
        }

        private void LoadVocabulary(string path)
        {
            var index = 0;
            foreach (var line in File.ReadLines(path))
            {
                var token = line.TrimEnd('\r', '\n');
                if (!_vocabulary.ContainsKey(token))
                {
                    _vocabulary[token] = index;
                }

                index++;
            }
        }

        private float[] EmbedOne(string text)
        {
            var tokens = Tokenize(text ?? string.Empty, Math.Max(16, Math.Min(512, _options.MaxTokens)));
            var ids = tokens.Select(TokenId).ToArray();
            var mask = Enumerable.Repeat(1L, ids.Length).ToArray();

            var inputs = new List<object>();
            AddTensor(inputs, _inputIdsName, ids);
            if (!string.IsNullOrWhiteSpace(_attentionMaskName))
            {
                AddTensor(inputs, _attentionMaskName, mask);
            }

            if (!string.IsNullOrWhiteSpace(_tokenTypeIdsName))
            {
                AddTensor(inputs, _tokenTypeIdsName, new long[ids.Length]);
            }

            var inputArray = Array.CreateInstance(_namedValueType, inputs.Count);
            for (var i = 0; i < inputs.Count; i++)
            {
                inputArray.SetValue(inputs[i], i);
            }

            object outputs;
            lock (_runGate)
            {
                // InferenceSession 不保证并发安全：前台重排与后台建索引可能同时调用
                outputs = _runMethod.Invoke(_session, new object[] { inputArray });
            }

            try
            {
                var first = FirstOutput(outputs);
                var tensor = first?.GetType().GetMethod("AsTensor")?.MakeGenericMethod(typeof(float)).Invoke(first, null);
                if (tensor is IEnumerable<float> values)
                {
                    return Pool(values.ToArray(), ids.Length, mask);
                }

                throw new InvalidOperationException("ONNX 输出不是 float 张量");
            }
            finally
            {
                (outputs as IDisposable)?.Dispose();
            }
        }

        private static object FirstOutput(object outputs)
        {
            if (outputs is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    return item;
                }
            }

            return null;
        }

        private void AddTensor(IList<object> inputs, string name, long[] data)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            object tensor;
            try
            {
                tensor = Activator.CreateInstance(_tensorType, data, new[] { 1, data.Length });
            }
            catch (Exception)
            {
                tensor = Activator.CreateInstance(_tensorType, data, new[] { 1, data.Length }, true);
            }

            var value = _createTensorMethod.MakeGenericMethod(typeof(long)).Invoke(null, new[] { name, tensor });
            inputs.Add(value);
        }

        private static float[] Pool(float[] flat, int tokenCount, long[] mask)
        {
            if (flat.Length == 0)
            {
                return [];
            }

            if (tokenCount <= 0 || flat.Length % tokenCount != 0)
            {
                return Normalize(flat);
            }

            var dimension = flat.Length / tokenCount;
            var pooled = new float[dimension];
            var count = 0;

            for (var t = 0; t < tokenCount; t++)
            {
                if (mask[t] == 0)
                {
                    continue;
                }

                var offset = t * dimension;
                for (var d = 0; d < dimension; d++)
                {
                    pooled[d] += flat[offset + d];
                }

                count++;
            }

            if (count == 0)
            {
                return [];
            }

            for (var d = 0; d < dimension; d++)
            {
                pooled[d] /= count;
            }

            return Normalize(pooled);
        }

        private static float[] Normalize(float[] vector)
        {
            double sum = 0;
            foreach (var value in vector)
            {
                sum += value * value;
            }

            var length = Math.Sqrt(sum);
            if (length <= 0.0000001)
            {
                return vector;
            }

            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / length);
            }

            return vector;
        }

        private long TokenId(string token)
        {
            if (token == "[CLS]")
            {
                return 101;
            }

            if (token == "[SEP]")
            {
                return 102;
            }

            if (token == "[PAD]")
            {
                return 0;
            }

            return _vocabulary.TryGetValue(token, out var id) ? id : 100; // [UNK]
        }

        /// <summary>
        /// 简化版 BERT WordPiece 分词：英文按词 + 子词，中文按单字，统一加 [CLS]/[SEP]
        /// </summary>
        private List<string> Tokenize(string text, int maxTokens)
        {
            var tokens = new List<string> { "[CLS]" };
            var word = new StringBuilder();

            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
                {
                    FlushWord(word, tokens);
                    if (!char.IsWhiteSpace(c))
                    {
                        AddToken(c.ToString(), tokens);
                    }

                    continue;
                }

                if (IsCjk(c))
                {
                    FlushWord(word, tokens);
                    AddToken(c.ToString(), tokens);
                    continue;
                }

                word.Append(char.ToLowerInvariant(c));
                if (word.Length >= 100)
                {
                    FlushWord(word, tokens);
                }
            }

            FlushWord(word, tokens);

            if (tokens.Count > maxTokens - 1)
            {
                tokens = tokens.Take(maxTokens - 1).ToList();
            }

            tokens.Add("[SEP]");
            return tokens;
        }

        private void FlushWord(StringBuilder word, IList<string> tokens)
        {
            if (word.Length == 0)
            {
                return;
            }

            var value = word.ToString();
            word.Clear();

            if (_vocabulary.ContainsKey(value))
            {
                tokens.Add(value);
                return;
            }

            var start = 0;
            while (start < value.Length)
            {
                var end = value.Length;
                string piece = null;
                while (end > start)
                {
                    var candidate = (start == 0 ? string.Empty : "##") + value.Substring(start, end - start);
                    if (_vocabulary.ContainsKey(candidate))
                    {
                        piece = candidate;
                        break;
                    }

                    end--;
                }

                if (piece == null)
                {
                    tokens.Add("[UNK]");
                    return;
                }

                tokens.Add(piece);
                start = end;
            }
        }

        private void AddToken(string token, IList<string> tokens)
        {
            tokens.Add(_vocabulary.ContainsKey(token) ? token : "[UNK]");
        }

        private static bool IsCjk(char c)
        {
            return (c >= 0x3400 && c <= 0x4DBF) ||
                   (c >= 0x4E00 && c <= 0x9FFF) ||
                   (c >= 0x3040 && c <= 0x30FF) ||
                   (c >= 0xAC00 && c <= 0xD7AF);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                (_session as IDisposable)?.Dispose();
                _session = null;
                _initialized = false;
            }
        }
    }
}
