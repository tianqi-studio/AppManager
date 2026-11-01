using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppManager.Models;

/// <summary>
/// 主题枚举的 JSON 转换器。新版写入 Dark / Light / System；
/// 旧版数据中的 LiquidGlass 映射为暗色，Native 映射为跟随系统。
/// </summary>
public sealed class ThemeModeJsonConverter : JsonConverter<ThemeMode>
{
    public override ThemeMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // 旧版数字值：0 = LiquidGlass，1 = Native。
            return reader.GetInt32() == 1 ? ThemeMode.System : ThemeMode.Dark;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"无法识别的主题值：{reader.TokenType}。");
        }

        return reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "system" or "native" => ThemeMode.System,
            // "LiquidGlass" 及未知值统一落到暗色，避免旧数据导致启动失败。
            _ => ThemeMode.Dark
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        ThemeMode value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ThemeMode.Light => "Light",
            ThemeMode.System => "System",
            _ => "Dark"
        });
    }
}
