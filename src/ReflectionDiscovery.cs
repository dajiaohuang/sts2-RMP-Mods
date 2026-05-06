using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace RemoveMultiplayerPlayerLimit;

/// <summary>
/// Runtime type discovery utility for finding unknown game types.
/// Used during development to identify classes, methods, and scene structures
/// that are not directly referenced in the mod.
///
/// All methods are safe to call at runtime — exceptions are caught and logged.
/// </summary>
internal static class ReflectionDiscovery
{
    /// <summary>
    /// Find all types across all loaded assemblies matching a predicate.
    /// </summary>
    internal static List<Type> FindTypes(Func<Type, bool> predicate)
    {
        List<Type> results = new();
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (predicate(type))
                            results.Add(type);
                    }
                }
                catch (ReflectionTypeLoadException) { }
                catch (TypeLoadException) { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ReflectionDiscovery.FindTypes failed: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Find types whose full name contains the given fragment (case-insensitive).
    /// </summary>
    internal static List<Type> FindTypesByName(string nameFragment)
    {
        return FindTypes(t => t.FullName != null &&
            t.FullName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Search for types matching multiple name patterns and log results.
    /// </summary>
    internal static void DiscoverAndLog(string[] patterns)
    {
        Log.Info("=== ReflectionDiscovery: Scanning for game types ===");
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string pattern in patterns)
        {
            List<Type> matches = FindTypesByName(pattern);
            if (matches.Count == 0)
            {
                Log.Info($"  [{pattern}] → no matches");
                continue;
            }
            foreach (Type t in matches)
            {
                if (seen.Add(t.FullName!))
                    Log.Info($"  [{pattern}] → {t.FullName}");
            }
        }
        Log.Info($"=== Discovery complete: {seen.Count} unique types found ===");
    }

    /// <summary>
    /// Dump all public members (methods, fields, properties, events) of a type to the log.
    /// </summary>
    internal static void DumpTypeInfo(Type type, bool includeInherited = false)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if (!includeInherited)
            flags |= BindingFlags.DeclaredOnly;

        Log.Info($"=== Type: {type.FullName} ===");

        foreach (PropertyInfo p in type.GetProperties(flags))
        {
            string access = $"{(p.CanRead ? "get" : "")}{(p.CanWrite ? "set" : "")}";
            Log.Info($"  Property: {p.PropertyType.Name} {p.Name} {{{access}}}");
        }
        foreach (MethodInfo m in type.GetMethods(flags)
            .Where(m => !m.IsSpecialName))
        {
            string args = string.Join(", ", m.GetParameters()
                .Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Log.Info($"  Method: {m.ReturnType.Name} {m.Name}({args})");
        }
        foreach (FieldInfo f in type.GetFields(flags))
        {
            Log.Info($"  Field: {f.FieldType.Name} {f.Name}");
        }
    }

    /// <summary>
    /// Recursively dump a Godot node tree, showing type name and child count per node.
    /// </summary>
    internal static void DumpNodeTree(Node root, int maxDepth = 5)
    {
        Log.Info("=== Node Tree ===");
        DumpNodeTreeRecursive(root, 0, maxDepth);
    }

    private static void DumpNodeTreeRecursive(Node node, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        string indent = new string(' ', depth * 2);
        string nodeInfo = $"{node.GetType().Name} (name=\"{node.Name}\", children={node.GetChildCount()})";
        Log.Info($"{indent}{nodeInfo}");

        // Also show unique-name accessible children
        foreach (Node child in node.GetChildren())
        {
            string unique = child.IsUniqueNameInOwner() || child.Name.ToString().StartsWith("%") ?
                " [UNIQUE]" : "";
            string childInfo = $"{child.GetType().Name} (name=\"{child.Name}\", children={child.GetChildCount()}){unique}";
            Log.Info($"{indent}  {childInfo}");
        }

        // Recurse one level deeper for key children
        if (depth < maxDepth - 1)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child.GetChildCount() > 0)
                    DumpNodeTreeRecursive(child, depth + 1, maxDepth);
            }
        }
    }

    /// <summary>
    /// Get the full type hierarchy of a node as a string.
    /// </summary>
    internal static string GetNodeTypeHierarchy(Node node)
    {
        List<string> types = new();
        Type? t = node.GetType();
        while (t != null)
        {
            types.Add(t.FullName ?? t.Name);
            t = t.BaseType;
        }
        return string.Join(" → ", types);
    }

    /// <summary>
    /// Discover all types implementing a given interface across loaded mod assemblies.
    /// Returns the type names for logging purposes.
    /// </summary>
    internal static List<Type> FindImplementationsOf(Type interfaceType)
    {
        return FindTypes(t =>
            t.IsClass && !t.IsAbstract &&
            t.GetInterfaces().Any(i => i == interfaceType));
    }

    /// <summary>
    /// Try to find a Godot scene path by trying common path patterns.
    /// Returns the first path that successfully loads a PackedScene.
    /// </summary>
    internal static string? FindScenePath(string[] candidatePaths)
    {
        foreach (string path in candidatePaths)
        {
            try
            {
                var scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Reuse);
                if (scene != null)
                {
                    Log.Info($"ReflectionDiscovery: found scene at '{path}'");
                    return path;
                }
            }
            catch { }
        }
        return null;
    }
}
