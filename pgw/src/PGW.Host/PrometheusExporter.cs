using System.Globalization;
using System.Text;
using PGW.Core;

namespace PGW.Host;

/// <summary>Minimal `/metrics` exporter (§8.3) — no client library dependency needed for this small a surface.</summary>
public static class PrometheusExporter
{
    public static string Export(GatewayEngine engine)
    {
        var sb = new StringBuilder();
        sb.Append("# TYPE pgw_tag_value gauge\n# TYPE pgw_tag_quality gauge\n");
        foreach (var t in engine.TagSpace.GetAll())
        {
            var tag = Escape(t.Definition.Id);
            if (t.Value is bool or short or ushort or int or uint or long or ulong or float or double)
                sb.Append($"pgw_tag_value{{tag=\"{tag}\"}} {Convert.ToDouble(t.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}\n");
            sb.Append($"pgw_tag_quality{{tag=\"{tag}\"}} {(t.Quality == TagQuality.Good ? 1 : 0)}\n");
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
