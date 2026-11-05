using System.Text;
using UnGodot.Variants;

namespace UnGodot.Resources;

public sealed class ResourceTextResult
{
    public string Text { get; set; } = string.Empty;
    public bool IsScene { get; set; }
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Writes a parsed binary resource back as Godot text: <c>.tres</c> for resources and
/// <c>.tscn</c> for <c>PackedScene</c>s (whose <c>_bundled</c> dictionary carries the whole
/// node tree).
/// </summary>
public static class ResourceTextWriter
{
    private const int FlagIdIsPath = 1 << 30;
    private const int FlagInstanceIsPlaceholder = 1 << 30;
    private const int FlagMask = (1 << 24) - 1;
    private const int NameMask = (1 << 18) - 1;
    private const int TypeInstanced = 0x7FFFFFFF;

    public static ResourceTextResult Write(BinaryResource resource)
    {
        BinaryObject? main = resource.Main;
        if (main is null)
        {
            throw new ResourceFormatException("the resource has no main object");
        }

        if (main.Type == "PackedScene" || resource.Type == "PackedScene")
        {
            Variant? bundled = resource.GetProperty("_bundled");
            if (bundled is not null && bundled.Type == VariantType.Dictionary)
            {
                return WriteScene(resource, bundled);
            }
        }

        return WriteResource(resource, main);
    }

    private static ResourceTextResult WriteResource(BinaryResource resource, BinaryObject main)
    {
        var result = new ResourceTextResult { Text = string.Empty, IsScene = false };
        var body = new StringBuilder(8192);
        int subCount = Math.Max(0, resource.Objects.Count - 1);
        int loadSteps = 1 + resource.ExternalResources.Count + subCount;

        string type = string.IsNullOrEmpty(main.Type) ? resource.Type : main.Type;
        body.Append("[gd_resource type=\"").Append(type).Append("\" load_steps=").Append(loadSteps).Append(" format=2]\n\n");

        for (int i = 0; i < resource.ExternalResources.Count; i++)
        {
            ExternalResourceEntry ext = resource.ExternalResources[i];
            body.Append("[ext_resource path=\"").Append(Escape(ext.Path)).Append("\" type=\"")
                .Append(ext.Type).Append("\" id=").Append(i + 1).Append("]\n");
        }

        if (resource.ExternalResources.Count > 0)
        {
            body.Append('\n');
        }

        var internalOrdinals = BuildInternalOrdinals(resource);

        for (int i = 0; i < resource.Objects.Count - 1; i++)
        {
            BinaryObject sub = resource.Objects[i];
            body.Append("[sub_resource type=\"").Append(sub.Type).Append("\" id=").Append(i + 1).Append("]\n");
            WriteProperties(body, sub, resource, result, internalOrdinals);
            body.Append('\n');
        }

        body.Append("[resource]\n");
        WriteProperties(body, main, resource, result, internalOrdinals);

        result.Text = body.ToString();
        return result;
    }

    private static void WriteProperties(
        StringBuilder body,
        BinaryObject obj,
        BinaryResource resource,
        ResourceTextResult result,
        IReadOnlyDictionary<int, int> internalOrdinals)
    {
        Func<Variant, string?> resolver = value => ResolveReference(value, resource, result, internalOrdinals);
        foreach (KeyValuePair<string, Variant> property in obj.Properties)
        {
            body.Append(property.Key).Append(" = ")
                .Append(VariantTextWriter.Write(property.Value, 3, GodotTextStyle.Script, resolver))
                .Append('\n');
        }
    }

    /// <summary>
    /// Maps an internal reference subindex (the number in <c>local://N</c>) to the ordinal of
    /// the object in <see cref="BinaryResource.Objects"/>. Godot assigns subindices starting
    /// at 1 when saving, so the table ordinal and the reference index usually differ by one.
    /// </summary>
    private static Dictionary<int, int> BuildInternalOrdinals(BinaryResource resource)
    {
        // Subindices are assigned sparsely by the engine (a file whose sub-resource was
        // already saved in another file can jump to local://37), so this must be a map.
        var ordinals = new Dictionary<int, int>();
        const string prefix = "local://";
        for (int i = 0; i < resource.InternalResources.Count && i < resource.Objects.Count; i++)
        {
            string path = resource.InternalResources[i].Path;

            // The main resource keeps its real path in the table; only unnamed sub-resources
            // are stored as local://N and can be referenced by that index.
            if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(path.AsSpan(prefix.Length), out int subindex))
            {
                continue;
            }

            ordinals.TryAdd(subindex, i);
        }

        return ordinals;
    }

    private static string? ResolveReference(
        Variant value,
        BinaryResource resource,
        ResourceTextResult result,
        IReadOnlyDictionary<int, int> internalOrdinals)
    {
        if (value.Type != VariantType.Object)
        {
            return null;
        }

        if (value.Value is not ResourceReference reference)
        {
            return "null";
        }

        switch (reference.Kind)
        {
            case ResourceRefKind.Empty:
                return "null";

            case ResourceRefKind.ExternalIndex:
                if (reference.Index >= 0 && reference.Index < resource.ExternalResources.Count)
                {
                    return $"ExtResource( {reference.Index + 1} )";
                }

                result.Notes.Add($"external resource index {reference.Index} is out of range");
                return "null";

            case ResourceRefKind.ExternalPath:
                for (int i = 0; i < resource.ExternalResources.Count; i++)
                {
                    if (string.Equals(resource.ExternalResources[i].Path, reference.Path, StringComparison.Ordinal))
                    {
                        return $"ExtResource( {i + 1} )";
                    }
                }

                result.Notes.Add($"missing external resource '{reference.Path}'");
                return $"ExtResource( \"" + Escape(reference.Path ?? string.Empty) + "\" )";

            case ResourceRefKind.Internal:
            {
                int ordinal = internalOrdinals.TryGetValue(reference.Index, out int mapped) ? mapped : -1;
                if (ordinal >= 0 && ordinal < resource.Objects.Count - 1)
                {
                    return $"SubResource( {ordinal + 1} )";
                }

                result.Notes.Add($"internal resource index {reference.Index} is out of range");
                return "null";
            }

            default:
                return "null";
        }
    }

    // -------------------------------------------------------------------------------------
    // Packed scenes
    // -------------------------------------------------------------------------------------

    private sealed class NodeData
    {
        public int Parent;
        public int Owner;
        public int Type;
        public int NameIndex;
        public int Index;
        public int Instance;
        public readonly List<(int Name, int Value)> Properties = [];
        public readonly List<int> Groups = [];
    }

    private static ResourceTextResult WriteScene(BinaryResource resource, Variant bundled)
    {
        var result = new ResourceTextResult { Text = string.Empty, IsScene = true };
        IReadOnlyList<KeyValuePair<Variant, Variant>> dictionary = bundled.AsDictionary();

        Variant? namesVariant = Lookup(dictionary, "names");
        Variant? variantsVariant = Lookup(dictionary, "variants");
        Variant? nodesVariant = Lookup(dictionary, "nodes");
        Variant? connsVariant = Lookup(dictionary, "conns");
        Variant? nodePathsVariant = Lookup(dictionary, "node_paths");
        Variant? editableVariant = Lookup(dictionary, "editable_instances");

        List<string> names = namesVariant?.Value is string[] strings ? strings.ToList() : [];
        IReadOnlyList<Variant> variants = variantsVariant?.Value as IReadOnlyList<Variant> ?? [];
        int[] nodesRaw = nodesVariant?.Value as int[] ?? [];
        int[] connsRaw = connsVariant?.Value as int[] ?? [];
        IReadOnlyList<Variant> nodePaths = nodePathsVariant?.Value as IReadOnlyList<Variant> ?? [];
        IReadOnlyList<Variant> editable = editableVariant?.Value as IReadOnlyList<Variant> ?? [];

        // Decode the node table (format documented in SceneState::set_bundled_scene).
        var nodes = new List<NodeData>();
        int cursor = 0;
        while (cursor < nodesRaw.Length)
        {
            var node = new NodeData
            {
                Parent = nodesRaw[cursor++],
                Owner = nodesRaw[cursor++],
                Type = nodesRaw[cursor++],
            };

            int nameIndex = nodesRaw[cursor++];
            node.NameIndex = nameIndex & NameMask;
            node.Index = (nameIndex >> 18) - 1;
            node.Instance = nodesRaw[cursor++];

            int propertyCount = nodesRaw[cursor++];
            for (int i = 0; i < propertyCount; i++)
            {
                node.Properties.Add((nodesRaw[cursor++], nodesRaw[cursor++]));
            }

            int groupCount = nodesRaw[cursor++];
            for (int i = 0; i < groupCount; i++)
            {
                node.Groups.Add(nodesRaw[cursor++]);
            }

            nodes.Add(node);
        }

        var connections = new List<(int From, int To, int Signal, int Method, int Flags, List<int> Binds)>();
        cursor = 0;
        while (cursor + 6 <= connsRaw.Length)
        {
            int from = connsRaw[cursor++];
            int to = connsRaw[cursor++];
            int signal = connsRaw[cursor++];
            int method = connsRaw[cursor++];
            int flags = connsRaw[cursor++];
            int bindCount = connsRaw[cursor++];
            var binds = new List<int>(bindCount);
            for (int i = 0; i < bindCount && cursor < connsRaw.Length; i++)
            {
                binds.Add(connsRaw[cursor++]);
            }

            connections.Add((from, to, signal, method, flags, binds));
        }

        // Paths: index-1 is the root; every node path is relative to it.
        var paths = new string[nodes.Count];
        for (int i = 1; i < nodes.Count; i++)
        {
            int parentId = nodes[i].Parent;
            string parentPath = (parentId & FlagIdIsPath) != 0
                ? NodePathToString(nodePaths, parentId & FlagMask)
                : paths[parentId & FlagMask] is { } p ? p : ".";

            string name = NameAt(names, nodes[i].NameIndex);
            paths[i] = parentPath == "." ? name : (paths[parentId & FlagMask] + "/" + name);
        }

        int subCount = Math.Max(0, resource.Objects.Count - 1);
        int loadSteps = 1 + resource.ExternalResources.Count + subCount;

        var body = new StringBuilder(16 * 1024);
        body.Append("[gd_scene load_steps=").Append(loadSteps).Append(" format=2]\n\n");

        for (int i = 0; i < resource.ExternalResources.Count; i++)
        {
            ExternalResourceEntry ext = resource.ExternalResources[i];
            body.Append("[ext_resource path=\"").Append(Escape(ext.Path)).Append("\" type=\"")
                .Append(ext.Type).Append("\" id=").Append(i + 1).Append("]\n");
        }

        if (resource.ExternalResources.Count > 0)
        {
            body.Append('\n');
        }

        var internalOrdinals = BuildInternalOrdinals(resource);

        for (int i = 0; i < resource.Objects.Count - 1; i++)
        {
            BinaryObject sub = resource.Objects[i];
            body.Append("[sub_resource type=\"").Append(sub.Type).Append("\" id=").Append(i + 1).Append("]\n");
            WriteProperties(body, sub, resource, result, internalOrdinals);
            body.Append('\n');
        }

        Func<Variant, string?> resolver = value => ResolveReference(value, resource, result, internalOrdinals);

        for (int i = 0; i < nodes.Count; i++)
        {
            NodeData node = nodes[i];
            string name = NameAt(names, node.NameIndex);
            body.Append("[node name=\"").Append(Escape(name)).Append('"');

            if (i == 0)
            {
                if (node.Instance < 0)
                {
                    body.Append(" type=\"").Append(NodeTypeAt(names, node.Type)).Append('"');
                }
            }
            else
            {
                body.Append(" parent=\"").Append(Escape(ParentPath(paths, nodePaths, node.Parent))).Append('"');
                if (node.Instance < 0 && node.Type != TypeInstanced)
                {
                    body.Append(" type=\"").Append(NodeTypeAt(names, node.Type)).Append('"');
                }

                if (node.Index >= 0)
                {
                    body.Append(" index=\"").Append(node.Index).Append('"');
                }
            }

            // Groups are part of the section header, on the same line as the node.
            if (node.Groups.Count > 0)
            {
                body.Append(" groups=[");
                for (int g = 0; g < node.Groups.Count; g++)
                {
                    if (g > 0)
                    {
                        body.Append(", ");
                    }

                    body.Append('"').Append(Escape(NameAt(names, node.Groups[g]))).Append('"');
                }

                body.Append(']');
            }

            if (node.Instance >= 0)
            {
                bool placeholder = (node.Instance & FlagInstanceIsPlaceholder) != 0;
                body.Append(placeholder ? " instance_placeholder=" : " instance=")
                    .Append(InstanceReference(node.Instance, variants, resolver));
            }

            body.Append("]\n");

            foreach ((int propertyName, int propertyValue) in node.Properties)
            {
                if (propertyValue < 0 || propertyValue >= variants.Count)
                {
                    result.Notes.Add($"node '{name}': property value {propertyValue} is out of range");
                    continue;
                }

                string key = NameAt(names, propertyName);
                Variant value = variants[propertyValue];
                body.Append(key).Append(" = ")
                    .Append(VariantTextWriter.Write(value, 3, GodotTextStyle.Script, resolver))
                    .Append('\n');
            }

            body.Append('\n');
        }

        foreach ((int from, int to, int signal, int method, int flags, List<int> binds) in connections)
        {
            body.Append("[connection signal=\"").Append(Escape(NameAt(names, signal)))
                .Append("\" from=\"").Append(Escape(ConnectionPath(paths, nodePaths, from)))
                .Append("\" to=\"").Append(Escape(ConnectionPath(paths, nodePaths, to)))
                .Append("\" method=\"").Append(Escape(NameAt(names, method))).Append('"');

            if (binds.Count > 0)
            {
                body.Append(" binds=[ ");
                for (int i = 0; i < binds.Count; i++)
                {
                    if (i > 0)
                    {
                        body.Append(", ");
                    }

                    if (binds[i] >= 0 && binds[i] < variants.Count)
                    {
                        body.Append(VariantTextWriter.Write(variants[binds[i]], 3, GodotTextStyle.Script, resolver));
                    }
                }

                body.Append(" ]");
            }

            body.Append("]\n");
        }

        foreach (Variant editablePath in editable)
        {
            body.Append("[editable path=\"").Append(Escape(editablePath.Value?.ToString() ?? string.Empty)).Append("\"]\n");
        }

        result.Text = body.ToString();
        return result;
    }

    private static string ParentPath(string[] paths, IReadOnlyList<Variant> nodePaths, int parentId)
    {
        if ((parentId & FlagIdIsPath) != 0)
        {
            return NodePathToString(nodePaths, parentId & FlagMask);
        }

        int index = parentId & FlagMask;
        if (index <= 0 || index >= paths.Length)
        {
            return ".";
        }

        return paths[index];
    }

    private static string ConnectionPath(string[] paths, IReadOnlyList<Variant> nodePaths, int id)
    {
        if ((id & FlagIdIsPath) != 0)
        {
            return NodePathToString(nodePaths, id & FlagMask);
        }

        int index = id & FlagMask;
        return index >= 0 && index < paths.Length ? paths[index] : ".";
    }

    private static string NodePathToString(IReadOnlyList<Variant> nodePaths, int index)
    {
        if (index >= 0 && index < nodePaths.Count && nodePaths[index].Value is NodePathValue path)
        {
            return path.ToString();
        }

        return ".";
    }

    private static string InstanceReference(int instance, IReadOnlyList<Variant> variants, Func<Variant, string?> resolver)
    {
        int index = instance & FlagMask;
        if (index < 0 || index >= variants.Count)
        {
            return "null";
        }

        Variant value = variants[index];
        return resolver(value) ?? VariantTextWriter.Write(value, 3, GodotTextStyle.Script, resolver);
    }

    private static string NameAt(List<string> names, int index)
    {
        int masked = index & NameMask;
        return masked >= 0 && masked < names.Count ? names[masked] : string.Empty;
    }

    private static string NodeTypeAt(List<string> names, int index)
    {
        string type = NameAt(names, index);
        return string.IsNullOrEmpty(type) ? "Node" : type;
    }

    private static Variant? Lookup(IReadOnlyList<KeyValuePair<Variant, Variant>> dictionary, string key)
    {
        foreach (KeyValuePair<Variant, Variant> entry in dictionary)
        {
            if (entry.Key.Type == VariantType.String && entry.Key.AsString() == key)
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static string Escape(string value) => VariantTextWriter.Escape(value);
}