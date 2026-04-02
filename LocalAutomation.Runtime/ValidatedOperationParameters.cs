using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides an operation-scoped view over a raw parameter bag. The wrapper keeps parameter storage operation-agnostic
/// while preventing operation code from accidentally accessing option sets that the current operation did not declare.
/// </summary>
public sealed class ValidatedOperationParameters
{
    private readonly HashSet<Type> _declaredOptionTypes;
    private readonly string _operationName;

    /// <summary>
    /// Creates one validated parameter view for a specific operation/parameter pair.
    /// </summary>
    public ValidatedOperationParameters(Operation operation, OperationParameters raw)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        OperationParameters operationParameters = raw ?? throw new ArgumentNullException(nameof(raw));
        Target = operationParameters.Target ?? throw new InvalidOperationException($"Operation '{operation.OperationName}' requires a target before accessing options.");
        _operationName = operation.OperationName;
        _declaredOptionTypes = operation.GetRequiredOptionSetTypes(Target).ToHashSet();
        _raw = operationParameters;
    }

    /// <summary>
    /// Creates one validated parameter view from explicit operation metadata stored alongside a built execution task.
    /// </summary>
    public ValidatedOperationParameters(string operationName, OperationParameters raw, IEnumerable<Type> declaredOptionTypes)
    {
        _operationName = string.IsNullOrWhiteSpace(operationName)
            ? throw new ArgumentException("Operation name is required.", nameof(operationName))
            : operationName;
        OperationParameters operationParameters = raw ?? throw new ArgumentNullException(nameof(raw));
        Target = operationParameters.Target ?? throw new InvalidOperationException($"Operation '{_operationName}' requires a target before accessing options.");
        _declaredOptionTypes = new HashSet<Type>((declaredOptionTypes ?? throw new ArgumentNullException(nameof(declaredOptionTypes))).Where(type => type != null));
        _raw = operationParameters;
    }

    private readonly OperationParameters _raw;

    /// <summary>
    /// Gets the validated target for the current operation.
    /// </summary>
    public IOperationTarget Target { get; }

    /// <summary>
    /// Gets the shared output-path override.
    /// </summary>
    public string? OutputPathOverride => _raw.OutputPathOverride;

    /// <summary>
    /// Gets the retry callback used by orchestration code that needs to surface transient failure handling without
    /// exposing the full raw parameter bag.
    /// </summary>
    public RetryHandler? RetryHandler => _raw.RetryHandler;

    /// <summary>
    /// Creates a raw child parameter bag from the validated source without exposing the stored raw instance directly.
    /// This keeps orchestration code able to fork child parameters while preserving the validated execution boundary.
    /// </summary>
    public OperationParameters CreateChild()
    {
        return _raw.CreateChild();
    }

    /// <summary>
    /// Returns the requested option set while enforcing that the owning operation declared it for the current target.
    /// </summary>
    public T GetOptions<T>() where T : OperationOptions
    {
        Type optionsType = typeof(T);
        if (!_declaredOptionTypes.Contains(optionsType))
        {
            throw new InvalidOperationException($"Operation '{_operationName}' attempted to access undeclared option set '{optionsType.FullName}'.");
        }

        return _raw.GetOptions<T>();
    }

    /// <summary>
    /// Tries to resolve one declared option set without throwing when the owning operation did not declare it.
    /// Use this only for optional behavior such as pass-through argument helpers.
    /// </summary>
    public bool TryGetOptions<T>(out T? options) where T : OperationOptions
    {
        if (!_declaredOptionTypes.Contains(typeof(T)))
        {
            options = null;
            return false;
        }

        options = _raw.GetOptions<T>();
        return true;
    }

    /// <summary>
    /// Returns the target cast to the requested type or throws when the operation reaches a path that depends on prior
    /// target validation.
    /// </summary>
    public TTarget GetTarget<TTarget>() where TTarget : IOperationTarget
    {
        if (Target is TTarget typedTarget)
        {
            return typedTarget;
        }

        throw new InvalidOperationException($"Operation '{_operationName}' requires a target of type '{typeof(TTarget).Name}'.");
    }
}
