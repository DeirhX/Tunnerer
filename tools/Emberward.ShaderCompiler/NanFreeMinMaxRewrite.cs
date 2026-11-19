namespace Emberward.ShaderCompiler;

/// <summary>
/// For the web only: turns GLSL.std.450 NMin, NMax and NClamp into FMin, FMax and FClamp, so SPIRV-Cross writes a plain
/// <c>min</c>, <c>max</c> or <c>clamp</c> instead of one wrapped in <c>isnan</c> tests.
/// </summary>
/// <remarks>
/// DXC emits the N forms for HLSL's <c>min</c>, <c>max</c>, <c>clamp</c> and <c>saturate</c>, which say what happens to
/// a NaN operand. GLSL ES has no such functions, so SPIRV-Cross spells each one as
/// <c>isnan(a) ? b : (isnan(b) ? a : max(a, b))</c>. ANGLE's Direct3D 11 backend hands that to FXC, and FXC took
/// minutes over the hundreds of them in PostPs and EntityPs, which is the hang Windows browsers showed while the game
/// loaded. The F forms differ only for NaN operands, which the shaders do not produce on purpose, and for which Direct3D
/// and Metal already return the other operand. Desktop keeps the original SPIR-V: this runs on a copy that only the
/// GLSL export sees.
/// </remarks>
internal static class NanFreeMinMaxRewrite
{
    private const int OpExtInstImport = 11;
    private const int OpExtInst = 12;
    private const int OpFunction = 54;

    private const uint FMin = 37, FMax = 40, FClamp = 43;
    private const uint NMin = 79, NMax = 80, NClamp = 81;

    private const int HeaderWords = 5;

    /// <summary>Returns the rewritten module, or <paramref name="spirv"/> itself when it has no N form to rewrite.</summary>
    public static byte[] Apply(byte[] spirv)
    {
        uint[] words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, words.Length * 4);
        if (words.Length < HeaderWords || words[0] != 0x07230203)
            throw new InvalidOperationException("Not a little-endian SPIR-V module.");

        uint glsl = 0;
        int rewritten = 0;
        for (int at = HeaderWords; at < words.Length; at += WordCount(words, at))
        {
            int op = (int)(words[at] & 0xFFFF);
            if (op == OpExtInstImport && ImportName(words, at) == "GLSL.std.450")
            {
                glsl = words[at + 1];
            }
            else if (op == OpExtInst && glsl != 0 && words[at + 3] == glsl)
            {
                // Result type, result, set, instruction, operands: the operands are the same for both forms.
                uint replacement = words[at + 4] switch
                {
                    NMin => FMin,
                    NMax => FMax,
                    NClamp => FClamp,
                    _ => 0,
                };
                if (replacement != 0)
                {
                    words[at + 4] = replacement;
                    rewritten++;
                }
            }
            else if (op == OpFunction && glsl == 0)
            {
                // Imports come before any function; a module that imports nothing has nothing to rewrite.
                return spirv;
            }
        }
        if (rewritten == 0)
            return spirv;

        byte[] result = new byte[spirv.Length];
        Buffer.BlockCopy(words, 0, result, 0, words.Length * 4);
        return result;
    }

    private static string ImportName(uint[] words, int at)
    {
        int count = WordCount(words, at);
        byte[] bytes = new byte[(count - 2) * 4];
        Buffer.BlockCopy(words, (at + 2) * 4, bytes, 0, bytes.Length);
        int end = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static int WordCount(uint[] words, int at)
    {
        int count = (int)(words[at] >> 16);
        if (count == 0 || at + count > words.Length)
            throw new InvalidOperationException($"Malformed SPIR-V instruction at word {at}.");
        return count;
    }
}
