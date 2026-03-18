using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAutomation.Persistence;

/// <summary>
/// Provides reusable JSON file persistence with optional token preprocessing so apps can centralize file I/O while
/// keeping state-specific serialization rules outside the store.
/// </summary>
public sealed class JsonFileStateStore<TState> where TState : class
{
    private readonly Func<TState> _createDefaultState;
    private readonly Func<JsonSerializer> _createSerializer;
    private readonly string _filePath;
    private readonly Action<Exception>? _onLoadException;
    private readonly Func<JToken, bool>? _preprocessToken;

    /// <summary>
    /// Creates a JSON-backed state store for a single file path.
    /// </summary>
    public JsonFileStateStore(
        string filePath,
        Func<TState> createDefaultState,
        Func<JsonSerializer> createSerializer,
        Func<JToken, bool>? preprocessToken = null,
        Action<Exception>? onLoadException = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _createDefaultState = createDefaultState ?? throw new ArgumentNullException(nameof(createDefaultState));
        _createSerializer = createSerializer ?? throw new ArgumentNullException(nameof(createSerializer));
        _preprocessToken = preprocessToken;
        _onLoadException = onLoadException;
    }

    /// <summary>
    /// Loads state from disk, falling back to a default instance when the file is missing or load fails.
    /// </summary>
    public JsonStateLoadResult<TState> Load()
    {
        if (!File.Exists(_filePath))
        {
            return new JsonStateLoadResult<TState>(_createDefaultState(), false);
        }

        try
        {
            string jsonText = File.ReadAllText(_filePath);
            JToken jsonToken = JToken.Parse(jsonText);
            bool wasModified = _preprocessToken?.Invoke(jsonToken) ?? false;

            JsonSerializer serializer = _createSerializer();
            TState? state = jsonToken.ToObject<TState>(serializer);
            return new JsonStateLoadResult<TState>(state ?? _createDefaultState(), wasModified);
        }
        catch (Exception ex)
        {
            _onLoadException?.Invoke(ex);
            return new JsonStateLoadResult<TState>(_createDefaultState(), false);
        }
    }

    /// <summary>
    /// Saves state atomically at the string level so serialization failures do not leave a partially written file.
    /// </summary>
    public void Save(TState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        StringBuilder stringBuilder = new();
        using StringWriter stringWriter = new(stringBuilder);
        using JsonTextWriter writer = new(stringWriter)
        {
            Formatting = Formatting.Indented
        };

        JsonSerializer serializer = _createSerializer();
        serializer.Serialize(writer, state);

        string directoryPath = Path.GetDirectoryName(_filePath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(_filePath, stringBuilder.ToString());
    }
}
