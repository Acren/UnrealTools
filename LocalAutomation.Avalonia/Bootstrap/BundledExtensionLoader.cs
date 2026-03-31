using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Avalonia.Bootstrap;

/// <summary>
/// Discovers bundled extensions from the app's output folder in a cross-platform way so the generic shell stays
/// runnable without hardcoding product-specific modules.
/// </summary>
public static class BundledExtensionLoader
{
    private const string ExtensionFolderName = "Extensions";
    private const string ExtensionAssemblyPattern = "LocalAutomation.Extensions.*.dll";

    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IExtensionModule).Assembly.GetName().Name ?? string.Empty,
        typeof(LocalAutomation.Runtime.ExecutionSession).Assembly.GetName().Name ?? string.Empty,
        typeof(Microsoft.Extensions.Logging.ILogger).Assembly.GetName().Name ?? string.Empty,
        typeof(Newtonsoft.Json.JsonConvert).Assembly.GetName().Name ?? string.Empty,
        // Property-grid adapters exchange PropertyModels collection/editor types with the host UI. These must come
        // from the default load context so the Avalonia property grid recognizes them as its native types instead of
        // treating plugin-created values as opaque objects.
        typeof(PropertyModels.Collections.CheckedList<>).Assembly.GetName().Name ?? string.Empty
    };

    /// <summary>
    /// Loads all bundled extension modules from the app's `Extensions` directory.
    /// </summary>
    public static ExtensionLoadResult LoadBundledExtensions()
    {
        ExtensionLoadResult result = new();
        string extensionsRoot = Path.Combine(AppContext.BaseDirectory, ExtensionFolderName);

        if (!Directory.Exists(extensionsRoot))
        {
            result.Warnings.Add($"Bundled extension folder '{extensionsRoot}' was not found.");
            return result;
        }

        foreach (string extensionDirectory in Directory.EnumerateDirectories(extensionsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            LoadExtensionDirectory(extensionDirectory, result);
        }

        return result;
    }

    /// <summary>
    /// Loads every candidate extension module from one bundled extension directory.
    /// </summary>
    private static void LoadExtensionDirectory(string extensionDirectory, ExtensionLoadResult result)
    {
        string[] candidateAssemblies = Directory.EnumerateFiles(extensionDirectory, ExtensionAssemblyPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidateAssemblies.Length == 0)
        {
            result.Warnings.Add($"No extension assembly matching '{ExtensionAssemblyPattern}' was found in '{extensionDirectory}'.");
            return;
        }

        foreach (string assemblyPath in candidateAssemblies)
        {
            LoadExtensionAssembly(assemblyPath, result);
        }
    }

    /// <summary>
    /// Loads one extension assembly and instantiates all of its module types.
    /// </summary>
    private static void LoadExtensionAssembly(string assemblyPath, ExtensionLoadResult result)
    {
        try
        {
            PluginLoadContext loadContext = new(assemblyPath);
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            (Type[] moduleTypes, string[] typeLoadErrors) = GetLoadableTypes(assembly);
            foreach (string typeLoadError in typeLoadErrors)
            {
                result.Errors.Add($"Failed to load one or more types from '{assemblyPath}': {typeLoadError}");
            }

            moduleTypes = moduleTypes
                .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(IExtensionModule).IsAssignableFrom(type))
                .ToArray();

            if (moduleTypes.Length == 0)
            {
                result.Warnings.Add($"Assembly '{assemblyPath}' did not contain any concrete '{nameof(IExtensionModule)}' implementations.");
                return;
            }

            foreach (Type moduleType in moduleTypes)
            {
                try
                {
                    if (Activator.CreateInstance(moduleType) is not IExtensionModule module)
                    {
                        result.Errors.Add($"Extension module type '{moduleType.FullName}' in '{assemblyPath}' could not be instantiated.");
                        continue;
                    }

                    result.Modules.Add(module);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to create extension module '{moduleType.FullName}' from '{assemblyPath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to load extension assembly '{assemblyPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Returns loadable types while preserving partial loader diagnostics as ordinary startup failures instead of hard
    /// crashing the entire application.
    /// </summary>
    private static (Type[] Types, string[] Errors) GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return (assembly.GetTypes(), Array.Empty<string>());
        }
        catch (ReflectionTypeLoadException ex)
        {
            string[] errors = ex.LoaderExceptions
                .Where(exception => exception != null)
                .Select(exception => exception!.Message)
                .ToArray();

            return (GetNonNullTypes(ex.Types), errors);
        }
    }

    /// <summary>
    /// Casts reflection-loaded types to a non-null array after null entries have already been filtered out.
    /// </summary>
    private static Type[] GetNonNullTypes(Type?[] types)
    {
        return Array.FindAll(types, type => type != null).Cast<Type>().ToArray();
    }

    /// <summary>
    /// Resolves plugin-local dependencies from the extension's own folder while sharing the core contracts with the
    /// default load context so type identity remains stable across the host/plugin boundary.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        /// <summary>
        /// Creates a load context rooted at the provided plugin assembly path.
        /// </summary>
        public PluginLoadContext(string pluginAssemblyPath)
            : base(isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        }

        /// <summary>
        /// Resolves plugin-local assemblies or falls back to the default context for shared contracts.
        /// </summary>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (SharedAssemblyNames.Contains(assemblyName.Name ?? string.Empty))
            {
                return null;
            }

            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath == null ? null : LoadFromAssemblyPath(assemblyPath);
        }
    }
}
