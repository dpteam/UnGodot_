using System.Buffers.Binary;
using System.Text;
using UnGodot.GDScript;
using UnGodot.Variants;
using Xunit;

namespace UnGodot.Tests;

public class GdScriptTests
{
    /// <summary>Builds a GDScript 1.0 <c>GDSC</c> container, token by token.</summary>
    private sealed class ScriptBuilder
    {
        private readonly BytecodeRevision _revision;
        private readonly List<string> _identifiers = [];
        private readonly List<byte[]> _constants = [];
        private readonly List<(int Token, int Line)> _lines = [];
        private readonly List<uint> _tokens = [];

        public ScriptBuilder(BytecodeRevision revision)
        {
            _revision = revision;
        }

        public int Identifier(string name)
        {
            _identifiers.Add(name);
            return _identifiers.Count - 1;
        }

        public int ConstantInt(long value)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), (int)value);
            _constants.Add(bytes);
            return _constants.Count - 1;
        }

        public int ConstantReal(double value)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 3);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), (float)value);
            _constants.Add(bytes);
            return _constants.Count - 1;
        }

        public int ConstantString(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            int padded = (utf8.Length + 3) & ~3;
            byte[] bytes = new byte[8 + padded];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), utf8.Length);
            utf8.CopyTo(bytes, 8);
            _constants.Add(bytes);
            return _constants.Count - 1;
        }

        public ScriptBuilder Line(int line)
        {
            _lines.Add((_tokens.Count, line));
            return this;
        }

        public ScriptBuilder Token(GlobalToken token, int payload = 0, int? line = null)
        {
            if (line is not null)
            {
                Line(line.Value);
            }

            int local = ((IReadOnlyList<GlobalToken>)_revision.TokenMap).ToList().IndexOf(token);
            if (local < 0)
            {
                throw new InvalidOperationException($"{token} is not used by {_revision.Rev}");
            }

            _tokens.Add((uint)(local | (payload << 8)));
            return this;
        }

        public ScriptBuilder Newline(int indent = 0)
        {
            return Token(GlobalToken.G_TK_NEWLINE, indent);
        }

        public byte[] Build()
        {
            var output = new List<byte>();
            output.AddRange("GDSC"u8.ToArray());
            output.AddRange(BitConverter.GetBytes((uint)_revision.Version));
            output.AddRange(BitConverter.GetBytes((uint)_identifiers.Count));
            output.AddRange(BitConverter.GetBytes((uint)_constants.Count));
            output.AddRange(BitConverter.GetBytes((uint)_lines.Count));
            output.AddRange(BitConverter.GetBytes((uint)_tokens.Count));

            foreach (string identifier in _identifiers)
            {
                byte[] raw = Encoding.UTF8.GetBytes(identifier);
                var encoded = new byte[raw.Length + 1];
                for (int i = 0; i < raw.Length; i++)
                {
                    encoded[i] = (byte)(raw[i] ^ 0xB6);
                }

                encoded[^1] = 0xB6; // the NUL terminator, also XORed
                output.AddRange(BitConverter.GetBytes((uint)encoded.Length));
                output.AddRange(encoded);
            }

            foreach (byte[] constant in _constants)
            {
                output.AddRange(constant);
            }

            foreach ((int token, int line) in _lines)
            {
                output.AddRange(BitConverter.GetBytes((uint)token));
                output.AddRange(BitConverter.GetBytes((uint)line));
            }

            foreach (uint token in _tokens)
            {
                if ((token & 0xFFFFFF80) == 0)
                {
                    output.Add((byte)token);
                }
                else
                {
                    output.AddRange(BitConverter.GetBytes(token | 0x80));
                }
            }

            return output.ToArray();
        }
    }

    private static BytecodeRevision Revision311() =>
        BytecodeVersionDatabase.ForRev("514a3fb") ?? throw new InvalidOperationException("missing 3.1.1 revision");

    [Fact]
    public void DecompilesASimpleScript()
    {
        BytecodeRevision revision = Revision311();
        var builder = new ScriptBuilder(revision);
        int node = builder.Identifier("Node");
        int x = builder.Identifier("x");
        int f = builder.Identifier("f");
        int ready = builder.Identifier("_ready");
        int one = builder.ConstantInt(1);
        int zero = builder.ConstantReal(0.0);

        builder
            .Line(1)
            .Token(GlobalToken.G_TK_PR_EXTENDS)
            .Token(GlobalToken.G_TK_IDENTIFIER, node)
            .Newline()
            .Line(2)
            .Token(GlobalToken.G_TK_PR_VAR)
            .Token(GlobalToken.G_TK_IDENTIFIER, x)
            .Token(GlobalToken.G_TK_OP_ASSIGN)
            .Token(GlobalToken.G_TK_CONSTANT, one)
            .Newline()
            .Line(3)
            .Token(GlobalToken.G_TK_PR_VAR)
            .Token(GlobalToken.G_TK_IDENTIFIER, f)
            .Token(GlobalToken.G_TK_OP_ASSIGN)
            .Token(GlobalToken.G_TK_CONSTANT, zero)
            .Newline()
            .Line(4)
            .Newline()
            .Line(5)
            .Token(GlobalToken.G_TK_PR_FUNCTION)
            .Token(GlobalToken.G_TK_IDENTIFIER, ready)
            .Token(GlobalToken.G_TK_PARENTHESIS_OPEN)
            .Token(GlobalToken.G_TK_PARENTHESIS_CLOSE)
            .Token(GlobalToken.G_TK_COLON)
            .Newline(1)
            .Line(6)
            .Token(GlobalToken.G_TK_BUILT_IN_FUNC, revision.FunctionNames.ToList().IndexOf("print"))
            .Token(GlobalToken.G_TK_PARENTHESIS_OPEN)
            .Token(GlobalToken.G_TK_IDENTIFIER, x)
            .Token(GlobalToken.G_TK_PARENTHESIS_CLOSE)
            .Newline()
            .Token(GlobalToken.G_TK_EOF);

        DecompileResult result = GdScriptDecompiler.Decompile(builder.Build(), revision);

        Assert.Equal("extends Node\nvar x = 1\nvar f = 0.0\n\nfunc _ready():\n\tprint(x)\n", result.Source);
    }

    [Fact]
    public void DetectsTheRevisionFromTheBytecodeVersion()
    {
        BytecodeRevision revision = Revision311();
        var builder = new ScriptBuilder(revision);
        int node = builder.Identifier("Node");
        builder.Line(1)
            .Token(GlobalToken.G_TK_PR_EXTENDS)
            .Token(GlobalToken.G_TK_IDENTIFIER, node)
            .Newline()
            .Token(GlobalToken.G_TK_EOF);

        byte[] bytecode = builder.Build();
        RevisionDetection detection = RevisionDetector.Detect(bytecode, "3.1");
        Assert.Equal(13, detection.BytecodeVersion);
        Assert.NotNull(detection.Revision);
        Assert.Equal("514a3fb", detection.Revision!.Rev);
    }

    [Fact]
    public void RejectsGodot4BytecodeWithAClearMessage()
    {
        byte[] buffer = new byte[32];
        "GDSC"u8.ToArray().CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 100);
        RevisionDetection detection = RevisionDetector.Detect(buffer);
        Assert.Null(detection.Revision);
        Assert.Contains("Godot 4", detection.Error);
    }

    [Fact]
    public void DecodesGodot3Variants()
    {
        // int 42
        Assert.Equal(42L, VariantReader.Decode(Convert.FromHexString("020000002A000000"), out int used).AsInt());
        Assert.Equal(8, used);

        // real 0.0
        Assert.Equal(0.0, VariantReader.Decode(Convert.FromHexString("0300000000000000"), out _).AsDouble());

        // "hi"
        Variant text = VariantReader.Decode(Convert.FromHexString("0400000002000000" + "68690000"), out _);
        Assert.Equal("hi", text.AsString());

        // [true, 7]
        Variant array = VariantReader.Decode(
            Convert.FromHexString("1300000002000000" + "0100000001000000" + "0200000007000000"), out _);
        Assert.Equal(2, array.AsArray().Count);
        Assert.True(array.AsArray()[0].AsBool());
        Assert.Equal(7L, array.AsArray()[1].AsInt());

        // {"a": 1}
        Variant dictionary = VariantReader.Decode(
            Convert.FromHexString("1200000001000000" + "0400000001000000" + "61000000" + "0200000001000000"), out _);
        Assert.Single(dictionary.AsDictionary());
        Assert.Equal("a", dictionary.AsDictionary()[0].Key.AsString());
        Assert.Equal(1L, dictionary.AsDictionary()[0].Value.AsInt());
    }

    [Fact]
    public void DecodesNodePaths()
    {
        // type 15, two names + one subname, relative path
        string hex = string.Concat(
            "0F000000", "02000080", "01000000", "00000000",
            "06000000", "506C617965720000", // "Player" padded
            "06000000", "5370726974650000", // "Sprite" padded
            "05000000", "76616C7565000000");  // "value" padded
        Variant path = VariantReader.Decode(Convert.FromHexString(hex), out _);
        Assert.Equal("Player/Sprite:value", path.Value!.ToString());
    }

    [Fact]
    public void WritesGodotText()
    {
        Assert.Equal("0.0", VariantTextWriter.Write(Variant.FromReal(0.0)));
        Assert.Equal("1.5", VariantTextWriter.Write(Variant.FromReal(1.5)));
        Assert.Equal("1.0", VariantTextWriter.Write(Variant.FromReal(1.0)));
        Assert.Equal("100", VariantTextWriter.Write(Variant.FromInt(100)));
        Assert.Equal("\"a\\nb\"", VariantTextWriter.Write(Variant.FromString("a\nb")));
        Assert.Equal(
            "Vector2( 1, 2 )",
            VariantTextWriter.Write(new Variant(VariantType.Vector2, new[] { 1f, 2f })));
        Assert.Equal(
            "@\"Player/Sprite\"",
            VariantTextWriter.Write(new Variant(VariantType.NodePath, new NodePathValue(["Player", "Sprite"], [], false))));
    }

    [Fact]
    public void ArgCountTablesLineUpWithFunctionNames()
    {
        foreach (BytecodeRevision revision in BytecodeVersionDatabase.Revisions)
        {
            if (revision.ArgCounts.Count == 0)
            {
                continue;
            }

            Assert.Equal(revision.FunctionNames.Count, revision.ArgCounts.Count);
        }
    }

    [Fact]
    public void BytecodeVersionTableCoversEveryRevision()
    {
        Assert.True(BytecodeVersionDatabase.Revisions.Count > 50);
        BytecodeRevision revision = Revision311();
        Assert.Equal(13, revision.Version);
        Assert.Contains("print", revision.FunctionNames);
        Assert.Equal(GlobalToken.G_TK_IDENTIFIER, revision.ToGlobalToken(1));
    }
}