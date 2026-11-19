namespace Emberward.ShaderCompiler;

/// <summary>
/// For the web only: turns every implicit-LOD sample in a SPIR-V module into an explicit sample of level 0, so the
/// GLSL that SPIRV-Cross writes calls <c>textureLod(s, uv, 0.0)</c> instead of <c>texture(s, uv)</c>.
/// </summary>
/// <remarks>
/// ANGLE's Direct3D 11 backend compiles WebGL shaders with FXC, which must unroll any loop that samples with implicit
/// derivatives unless it can prove the loop uniform. On PostPs that compile does not finish. The web build gives every
/// texture level 0 only, with non-mipmapped filters (webgl.js), so both samples read the same texels. Desktop keeps
/// the original SPIR-V: this runs on a copy that only the GLSL export sees.
/// </remarks>
internal static class ExplicitLodRewrite
{
    private const int OpTypeFloat = 22;
    private const int OpConstant = 43;
    private const int OpFunction = 54;
    private const int OpImageSampleImplicitLod = 87;
    private const int OpImageSampleDrefImplicitLod = 89;
    private const int OpImageSampleProjImplicitLod = 91;
    private const int OpImageSampleProjDrefImplicitLod = 93;
    private const int OpImageQueryLod = 105;
    private const int OpImageSparseSampleImplicitLod = 305;
    private const int OpImageSparseSampleProjDrefImplicitLod = 312;

    private const uint BiasOperand = 0x1;
    private const uint LodOperand = 0x2;
    private const uint ConstOffsetOperand = 0x8;
    private const uint OffsetOperand = 0x10;

    private const int HeaderWords = 5;

    /// <summary>Returns the rewritten module, or <paramref name="spirv"/> itself when it samples nothing implicitly.</summary>
    public static byte[] Apply(byte[] spirv)
    {
        uint[] words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, words.Length * 4);
        if (words.Length < HeaderWords || words[0] != 0x07230203)
            throw new InvalidOperationException("Not a little-endian SPIR-V module.");

        uint floatType = 0, zero = 0;
        int floatTypeEnd = -1, implicitSamples = 0;
        for (int at = HeaderWords; at < words.Length; at += WordCount(words, at))
        {
            int op = (int)(words[at] & 0xFFFF);
            if (op == OpTypeFloat && words[at + 2] == 32)
            {
                floatType = words[at + 1];
                floatTypeEnd = at + WordCount(words, at);
            }
            else if (op == OpConstant && floatType != 0 && words[at + 1] == floatType && words[at + 3] == 0)
            {
                // 0.0f; -0.0f has the sign bit set and is not matched.
                zero = words[at + 2];
            }
            else if (op is OpImageSampleImplicitLod or OpImageSampleDrefImplicitLod
                     or OpImageSampleProjImplicitLod or OpImageSampleProjDrefImplicitLod)
            {
                implicitSamples++;
            }
            else if (op == OpImageQueryLod || op is >= OpImageSparseSampleImplicitLod and <= OpImageSparseSampleProjDrefImplicitLod)
            {
                throw new InvalidOperationException(
                    $"SPIR-V opcode {op} needs implicit derivatives; the web export cannot make it explicit.");
            }
        }
        if (implicitSamples == 0)
            return spirv;
        if (floatType == 0)
            throw new InvalidOperationException("A shader that samples a texture declares no 32-bit float type.");

        var output = new List<uint>(words.Length + implicitSamples * 2 + 4);
        output.AddRange(words.AsSpan(0, HeaderWords));
        for (int at = HeaderWords; at < words.Length;)
        {
            int count = WordCount(words, at);
            int op = (int)(words[at] & 0xFFFF);
            if (zero == 0 && at == floatTypeEnd)
            {
                // Declare 0.0f right after its type, which keeps it inside the types and constants section.
                zero = output[3]++;
                output.Add((4u << 16) | OpConstant);
                output.Add(floatType);
                output.Add(zero);
                output.Add(0);
            }
            if (op is OpImageSampleImplicitLod or OpImageSampleDrefImplicitLod
                or OpImageSampleProjImplicitLod or OpImageSampleProjDrefImplicitLod)
            {
                // Result type, result, sampled image, coordinate, then Dref for the depth-comparison forms.
                bool dref = op is OpImageSampleDrefImplicitLod or OpImageSampleProjDrefImplicitLod;
                int fixedWords = dref ? 6 : 5;
                uint mask = count > fixedWords ? words[at + fixedWords] : 0;
                if ((mask & ~(BiasOperand | ConstOffsetOperand | OffsetOperand)) != 0)
                    throw new InvalidOperationException($"Image operands 0x{mask:X} cannot be made explicit-LOD.");
                // Operands follow the mask in the order of their bits: Bias, (Lod), ConstOffset, Offset. Bias picks a
                // mip level, and level 0 is the only one there is, so it is dropped.
                var operands = new List<uint> { zero };
                int next = at + fixedWords + 1;
                if ((mask & BiasOperand) != 0)
                    next++;
                operands.AddRange(words.AsSpan(next, at + count - next));
                uint newMask = (mask & ~BiasOperand) | LodOperand;

                output.Add((uint)((fixedWords + 1 + operands.Count) << 16) | (uint)(op + 1)); // the ExplicitLod form
                output.AddRange(words.AsSpan(at + 1, fixedWords - 1));
                output.Add(newMask);
                output.AddRange(operands);
            }
            else
            {
                output.AddRange(words.AsSpan(at, count));
            }
            at += count;
            if (op == OpFunction && zero == 0)
                throw new InvalidOperationException("Could not place a 0.0 constant before the first function.");
        }

        byte[] result = new byte[output.Count * 4];
        Buffer.BlockCopy(output.ToArray(), 0, result, 0, result.Length);
        return result;
    }

    private static int WordCount(uint[] words, int at)
    {
        int count = (int)(words[at] >> 16);
        if (count == 0 || at + count > words.Length)
            throw new InvalidOperationException($"Malformed SPIR-V instruction at word {at}.");
        return count;
    }
}
