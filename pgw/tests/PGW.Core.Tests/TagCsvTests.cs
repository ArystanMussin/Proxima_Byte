using PGW.Core;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>§7.1 / §13: CSV tag import/export round-trip, including the quoting edge cases a
/// real spreadsheet tool would produce.</summary>
public class TagCsvTests
{
    [Fact]
    public void ParseCsv_Reads_A_Simple_Modbus_Style_Sheet()
    {
        var csv = "name,area,address,type,units\r\n" +
                  "T1_supply,HR,100,float32,°C\r\n" +
                  "pump1_run,DI,0,bool,\r\n";

        var rows = TagCsv.ParseCsv(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal("T1_supply", rows[0]["name"]);
        Assert.Equal("HR", rows[0]["area"]);
        Assert.Equal("100", rows[0]["address"]);
        Assert.Equal("°C", rows[0]["units"]);
        Assert.Equal("pump1_run", rows[1]["name"]);
        Assert.False(rows[1].ContainsKey("units")); // blank cell -> key simply absent, not an empty string
    }

    [Fact]
    public void ParseCsv_Strips_Leading_Bom_And_Handles_Quoted_Fields_With_Embedded_Comma_And_Quote()
    {
        var csv = "﻿name,description\r\n" +
                  "flow_total,\"supply, m3/h\"\r\n" +
                  "alarm,\"says \"\"hi\"\"\"\r\n";

        var rows = TagCsv.ParseCsv(csv);

        Assert.Equal("flow_total", rows[0]["name"]);
        Assert.Equal("supply, m3/h", rows[0]["description"]);
        Assert.Equal("says \"hi\"", rows[1]["description"]);
    }

    [Fact]
    public void ParseCsv_Tolerates_Trailing_Blank_Lines()
    {
        var rows = TagCsv.ParseCsv("name,type\r\nfoo,bool\r\n\r\n\r\n");
        Assert.Single(rows);
    }

    [Fact]
    public void ExportCsv_Then_ParseCsv_Round_Trips_Heterogeneous_Rows()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "modbus_tag", ["area"] = "HR", ["address"] = 100 },
            new() { ["name"] = "opcua_tag", ["node_id"] = "ns=2;s=Foo", ["description"] = "has, a comma" },
        };

        var csv = TagCsv.ExportCsv(rows);
        Assert.StartsWith("﻿", csv);
        Assert.Contains("name,area,address,node_id,description", csv);

        var reparsed = TagCsv.ParseCsv(csv);
        Assert.Equal(2, reparsed.Count);
        Assert.Equal("modbus_tag", reparsed[0]["name"]);
        Assert.Equal("HR", reparsed[0]["area"]);
        Assert.Equal("100", reparsed[0]["address"]);
        Assert.False(reparsed[0].ContainsKey("node_id"));
        Assert.Equal("opcua_tag", reparsed[1]["name"]);
        Assert.Equal("ns=2;s=Foo", reparsed[1]["node_id"]);
        Assert.Equal("has, a comma", reparsed[1]["description"]);
    }
}
