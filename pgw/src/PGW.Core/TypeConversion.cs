namespace PGW.Core;

public static class TagTypeConversion
{
    public static object? DefaultValue(TagDataType type) => type switch
    {
        TagDataType.Bool => false,
        TagDataType.String => null,
        TagDataType.Float32 => 0f,
        TagDataType.Float64 => 0d,
        TagDataType.Int64 => 0L,
        TagDataType.UInt64 => 0UL,
        TagDataType.UInt32 => 0U,
        TagDataType.Int32 => 0,
        TagDataType.UInt16 => (ushort)0,
        TagDataType.Int16 => (short)0,
        _ => 0,
    };

    public static object? Coerce(object? value, TagDataType type)
    {
        if (value is null) return null;
        try
        {
            return type switch
            {
                TagDataType.Bool => Convert.ToBoolean(value),
                TagDataType.Int16 => Convert.ToInt16(value),
                TagDataType.UInt16 => Convert.ToUInt16(value),
                TagDataType.Int32 => Convert.ToInt32(value),
                TagDataType.UInt32 => Convert.ToUInt32(value),
                TagDataType.Int64 => Convert.ToInt64(value),
                TagDataType.UInt64 => Convert.ToUInt64(value),
                TagDataType.Float32 => Convert.ToSingle(value),
                TagDataType.Float64 => Convert.ToDouble(value),
                TagDataType.String => value.ToString(),
                _ => value,
            };
        }
        catch (Exception) when (type != TagDataType.String)
        {
            return DefaultValue(type);
        }
    }
}
