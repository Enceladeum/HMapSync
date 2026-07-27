using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace HMSync.Services;

/// <summary>
/// Reflection-based struct/type inspector. Given a set of fully-qualified type names, walks
/// each type and dumps its fields (with [FieldOffset] where present), properties, and methods
/// (with parameter signatures) to a text file. Pure reflection — no game state touched, safe
/// to run anytime. Built to answer "does this FFXIVClientStructs type expose a visibility/
/// SetActive member in the bundled CS version?" without guessing, but reusable for any type.
/// </summary>
public static class StructDumper
{
    // Types we want to inspect. Add/remove freely. We try several namespace candidates per
    // simple name because FFXIVClientStructs moves things between namespaces across versions —
    // whichever resolves wins, and the dump notes which one matched.
    private static readonly string[] TargetTypes =
    {
        // ── Chair-sit / CPose investigation (active) ──
        // The seated "pulled backward" bug correlates with the pose-change event (timeline
        // 643), not our position offset. We want to know what controls seated pose + its
        // seat-relative placement: the Character, its CPoseState, the EmoteController, and
        // the Timeline container (where PlayTimeline lives and where a pose/mode field may sit).
        "FFXIVClientStructs.FFXIV.Client.Game.Character.Character",
        "FFXIVClientStructs.FFXIV.Client.Game.Character.EmoteController",
        "FFXIVClientStructs.FFXIV.Client.Game.Character.ActionTimelineSequencer",
        "FFXIVClientStructs.FFXIV.Client.Game.Character.TimelineContainer",
        "FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject",
    };

    public static string Dump(string outputDirectory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HMSync StructDumper — " + DateTime.Now.ToString("u"));
        sb.AppendLine("Resolving against the FFXIVClientStructs your plugin actually compiled with.");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine();

        // Search every loaded assembly for each type name (don't assume which assembly hosts it).
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        // De-dup: several candidate names may resolve to the same Type.
        var seen = new HashSet<Type>();

        foreach (var name in TargetTypes)
        {
            var type = ResolveType(name, assemblies);
            if (type == null) continue;
            if (!seen.Add(type)) continue;

            DumpType(sb, type, name);
            sb.AppendLine();
        }

        if (seen.Count == 0)
        {
            sb.AppendLine("NONE of the candidate type names resolved. Either the namespaces");
            sb.AppendLine("differ in this CS version, or the assembly isn't loaded yet. Tell");
            sb.AppendLine("Claude the build error from the IntelliSense probe instead.");
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "hmsync-structdump.log");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static Type? ResolveType(string fullName, Assembly[] assemblies)
    {
        // Try direct resolution first (fast path).
        var t = Type.GetType(fullName);
        if (t != null) return t;

        // Then scan loaded assemblies by full name (handles Outer+Nested via reflection name).
        foreach (var asm in assemblies)
        {
            try
            {
                t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            catch { /* dynamic/reflection-only assemblies can throw; skip */ }
        }

        // Nested-type fallback: "Ns.Outer+Inner" — resolve Outer, then GetNestedType(Inner).
        if (fullName.Contains('+'))
        {
            var split = fullName.Split('+');
            var outer = ResolveType(split[0], assemblies);
            if (outer != null)
            {
                var nested = outer.GetNestedType(split[1],
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static void DumpType(StringBuilder sb, Type type, string requestedName)
    {
        sb.AppendLine(new string('-', 78));
        sb.AppendLine("TYPE: " + type.FullName);
        sb.AppendLine("  (requested as: " + requestedName + ")");
        sb.AppendLine("  assembly: " + type.Assembly.GetName().Name);
        sb.AppendLine("  kind: " + (type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class")
                      + (type.IsAbstract && !type.IsInterface ? " abstract" : ""));
        if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
            sb.AppendLine("  base: " + type.BaseType.FullName);
        sb.AppendLine();

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.DeclaredOnly;

        // ── Fields (with offsets where the struct uses explicit layout) ──
        var fields = type.GetFields(flags);
        if (fields.Length > 0)
        {
            sb.AppendLine("  FIELDS:");
            foreach (var f in fields.OrderBy(GetOffsetForSort))
            {
                var off = f.GetCustomAttribute<FieldOffsetAttribute>();
                var offStr = off != null ? "0x" + off.Value.ToString("X").PadLeft(4, '0') : "  ----";
                sb.AppendLine($"    [{offStr}] {TypeName(f.FieldType)} {f.Name}"
                              + (f.IsStatic ? "  (static)" : ""));

                // One-level expansion: if a field is itself a value-type struct from the
                // FFXIVClientStructs assembly (e.g. EmoteController's sub-structs), list ITS
                // fields/methods inline so we don't have to chase each as a separate target.
                // Reflection lists struct fields fine; it's the manual targeting that's tedious.
                var ft = f.FieldType;
                if (ft.IsValueType && !ft.IsPrimitive && !ft.IsEnum
                    && ft.Namespace != null && ft.Namespace.StartsWith("FFXIVClientStructs")
                    && ft != type)
                {
                    foreach (var sf in ft.GetFields(flags).OrderBy(GetOffsetForSort))
                    {
                        var soff = sf.GetCustomAttribute<FieldOffsetAttribute>();
                        var soffStr = soff != null ? "0x" + soff.Value.ToString("X").PadLeft(4, '0') : "  ----";
                        sb.AppendLine($"        └ [{soffStr}] {TypeName(sf.FieldType)} {sf.Name}");
                    }
                    foreach (var sm in ft.GetMethods(flags).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
                    {
                        var ps = string.Join(", ", sm.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name));
                        sb.AppendLine($"        └ {TypeName(sm.ReturnType)} {sm.Name}({ps})");
                    }
                }
            }
            sb.AppendLine();
        }

        // ── Properties ──
        var props = type.GetProperties(flags);
        if (props.Length > 0)
        {
            sb.AppendLine("  PROPERTIES:");
            foreach (var p in props.OrderBy(p => p.Name))
                sb.AppendLine($"    {TypeName(p.PropertyType)} {p.Name} "
                              + "{" + (p.CanRead ? " get;" : "") + (p.CanWrite ? " set;" : "") + " }");
            sb.AppendLine();
        }

        // ── Methods (skip property accessors) — this is where SetActive/SetVisible would be ──
        var methods = type.GetMethods(flags)
                          .Where(m => !m.IsSpecialName)
                          .OrderBy(m => m.Name);
        var methodList = methods.ToList();
        if (methodList.Count > 0)
        {
            sb.AppendLine("  METHODS:");
            foreach (var m in methodList)
            {
                var ps = string.Join(", ", m.GetParameters()
                    .Select(p => TypeName(p.ParameterType) + " " + p.Name));
                sb.AppendLine($"    {TypeName(m.ReturnType)} {m.Name}({ps})"
                              + (m.IsStatic ? "  (static)" : ""));
            }
            sb.AppendLine();
        }

        // ── Highlight anything visibility-ish so it's easy to spot in the dump ──
        var hits = new List<string>();
        foreach (var f in fields)
            if (LooksVisibility(f.Name)) hits.Add("field " + f.Name);
        foreach (var p in props)
            if (LooksVisibility(p.Name)) hits.Add("property " + p.Name);
        foreach (var m in methodList)
            if (LooksVisibility(m.Name)) hits.Add("method " + m.Name + "(...)");
        if (hits.Count > 0)
        {
            sb.AppendLine("  >>> VISIBILITY-LIKELY MEMBERS: " + string.Join(", ", hits));
            sb.AppendLine();
        }
    }

    private static bool LooksVisibility(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("active") || n.Contains("visib") || n.Contains("draw")
               || n.Contains("render") || n.Contains("show") || n.Contains("hide")
               || n.Contains("enable") || n.Contains("disable");
    }

    private static int GetOffsetForSort(FieldInfo f)
    {
        var off = f.GetCustomAttribute<FieldOffsetAttribute>();
        return off?.Value ?? int.MaxValue; // unoffset fields (static/managed) sort last
    }

    private static string TypeName(Type t)
    {
        if (t.IsPointer) return TypeName(t.GetElementType()!) + "*";
        if (t.IsByRef) return "ref " + TypeName(t.GetElementType()!);
        // Strip the noisy namespace for readability but keep nested/generic clarity.
        var n = t.Name;
        if (t.IsGenericType)
        {
            var bare = n.Substring(0, n.IndexOf('`'));
            var args = string.Join(", ", t.GetGenericArguments().Select(TypeName));
            return bare + "<" + args + ">";
        }
        return n;
    }
}
