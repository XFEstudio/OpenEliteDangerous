using System.Globalization;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Client.Storage;

/// <summary>A three-component native double vector, without inferred units or rotation conventions.</summary>
public readonly record struct NativeLocationVector3([property: JsonRequired] double X, [property: JsonRequired] double Y,
    [property: JsonRequired] double Z);

/// <summary>
/// The confirmed eight-field location text. RotationVector is the input to native
/// helper RVA 0x86D8E0, not a claim that its components are Euler angles.
/// FrameFlag includes the addressed body's axial rotation when true. It does not
/// select galactic coordinates or assert a valid world/spawn position.
/// </summary>
public readonly record struct NativeLocationStringValue(
    [property: JsonRequired] ulong Address,
    [property: JsonRequired] NativeLocationVector3 Position,
    [property: JsonRequired] NativeLocationVector3 RotationVector,
    [property: JsonRequired] bool FrameFlag);

/// <summary>
/// Reads both branches of native RVA 0x3C2E900:
/// UInt64 address, three position doubles, three opaque rotation-vector doubles,
/// and a UInt32 flag whose nonzero values map to true. This component does not
/// create a world or validate that an address exists. The thirteen-token upload
/// branch packs sector X/Y/Z, mass code, system index and body index before the
/// same position/rotation/flag. Native writers include spaces after commas.
/// </summary>
public static class NativeLocationStringCodec
{
    // Local input limits, not restrictions inferred from the native address space.
    public const int MaximumTextLength = 512;
    public const int MaximumTokenLength = 64;

    private const NumberStyles FloatingPointStyle = NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    public static NativeLocationStringValue Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (TryParse(value, out var location)) return location;
        throw new FormatException("Expected eight or thirteen bounded native location fields.");
    }

    /// <summary>
    /// Accepts a strict subset of the permissive native numeric parsers: complete
    /// decimal tokens only, without grouping, whitespace, nonfinite values or
    /// ignored trailing text. A rejected value leaves location at its default.
    /// </summary>
    public static bool TryParse(string? value, out NativeLocationStringValue location)
    {
        location = default;
        if (value is null || value.Length > MaximumTextLength) return false;
        var text = value.AsSpan();
        Span<Range> tokens = stackalloc Range[14];
        var count=text.Split(tokens, ',', StringSplitOptions.None);
        if(count is not(8 or 13))return false;
        // Preserve the older strict eight-field contract. Only the confirmed
        // packed writer emits ASCII spaces around tokens.
        for (var i = 0; i < count; i++)
        {
            var part=text[tokens[i]];
            if(count==13)part=part.Trim(' ');
            var length = part.Length;
            if (length is < 1 or > MaximumTokenLength) return false;
        }
        ulong address;var componentStart=count==8?1:6;
        if(count==8)
        {if(!ulong.TryParse(text[tokens[0]],NumberStyles.None,CultureInfo.InvariantCulture,out address))return false;}
        else
        {
            Span<uint> packed=stackalloc uint[6];
            for(var i=0;i<packed.Length;i++)
                if(!uint.TryParse(text[tokens[i]].Trim(' '),NumberStyles.None,CultureInfo.InvariantCulture,out packed[i]))return false;
            var mass=packed[3];
            // Bounded nonoverlapping address fields; never emulate native
            // overflow/truncation on malformed input.
            if(mass>7||packed[0]>0x3fff||packed[1]>0x1fff||packed[2]>0x3fff||packed[5]>0x1ff
                ||packed[4]>((1UL<<(11+3*(int)mass))-1))return false;
            var m=(int)mass;
            var alignment=(1u<<m)-1;
            if(((packed[0]|packed[1]|packed[2])&alignment)!=0)return false;
            address=((ulong)(packed[0]>>m)<<(30-2*m))|((ulong)(packed[1]>>m)<<(17-m))
                |((ulong)(packed[2]>>m)<<3)|mass|((ulong)packed[4]<<(44-3*m))|((ulong)packed[5]<<55);
        }
        var flagText=text[tokens[count-1]];
        if(count==13)flagText=flagText.Trim(' ');
        if (!uint.TryParse(flagText, NumberStyles.None, CultureInfo.InvariantCulture, out var flag))
            return false;

        Span<double> components = stackalloc double[6];
        for (var i = 0; i < components.Length; i++)
        {
            var part=text[tokens[i+componentStart]];
            if(count==13)part=part.Trim(' ');
            if (!double.TryParse(part, FloatingPointStyle, CultureInfo.InvariantCulture, out var component)
                || !double.IsFinite(component))
                return false;
            components[i] = component;
        }
        location = new(address, new(components[0], components[1], components[2]),
            new(components[3], components[4], components[5]), flag != 0);
        return true;
    }

    /// <summary>Inverse of the pinned 1C5D920/23F8220 packed upload writers.</summary>
    public static string FormatPacked(NativeLocationStringValue value)
    {
        RequireFinite(value.Position,nameof(value));RequireFinite(value.RotationVector,nameof(value));
        var mass=(int)(value.Address&7);var systemBits=44-3*mass;
        var sector=value.Address&((1UL<<systemBits)-1);
        var x=((sector>>(30-2*mass))<<mass)&0x3fff;
        var y=((sector>>(17-mass))<<mass)&0x1fff;
        var z=((sector>>3)<<mass)&0x3fff;
        var system=(value.Address>>systemBits)&((1UL<<(11+3*mass))-1);
        return string.Create(CultureInfo.InvariantCulture,$"{x}, {y}, {z}, {mass}, {system}, {value.Address>>55}, {value.Position.X:R}, {value.Position.Y:R}, {value.Position.Z:R}, {value.RotationVector.X:R}, {value.RotationVector.Y:R}, {value.RotationVector.Z:R}, {(value.FrameFlag?1:0)}");
    }

    /// <summary>
    /// Writes invariant round-trip double text, without normalizing the rotation
    /// vector or narrowing the address. FrameFlag is canonicalized to 0 or 1.
    /// </summary>
    public static string Format(NativeLocationStringValue value)
    {
        RequireFinite(value.Position, nameof(value));
        RequireFinite(value.RotationVector, nameof(value));
        return string.Create(CultureInfo.InvariantCulture,
            $"{value.Address},{value.Position.X:R},{value.Position.Y:R},{value.Position.Z:R},{value.RotationVector.X:R},{value.RotationVector.Y:R},{value.RotationVector.Z:R},{(value.FrameFlag ? 1 : 0)}");
    }

    private static void RequireFinite(NativeLocationVector3 vector, string parameter)
    {
        if (!double.IsFinite(vector.X) || !double.IsFinite(vector.Y) || !double.IsFinite(vector.Z))
            throw new ArgumentOutOfRangeException(parameter, "Native location components must be finite doubles.");
    }
}
