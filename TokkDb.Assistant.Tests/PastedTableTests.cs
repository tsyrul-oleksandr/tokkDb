using System.Text;
using TokkDb.Assistant.Ingestion;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Found on 2026-09-15: a table pasted into the composer from a page came with its rows wrapped
/// across lines, went down the prose path, and the model's answer was cut off. A wrapped row is
/// put back together by the file reader, and a table in the message is read as a table.
/// </summary>
public sealed class PastedTableTests
{
    private const string Pasted = """
        Customer;Email;Date; Subscription;Country;Type;Email Status
        John Doe;oleksandr@example.com;08/28/2026
        09:29:36;Курси роботи з Windows;Ukraine;Dunning; Sent
        John Doe;test@example.com;08/28/2026 09:29:52;Курси роботи з
        Windows;Ukraine;Dunning; Sent
        John Doe;test@example.com;08/28/2026 09:30:00;Курси роботи з
        Windows;Ukraine;Dunning;Sent
        Jane Roe;jane@example.com;08/28/2026 09:31:00;Курси роботи з Windows;Ukraine;Dunning;Sent
        """;

    [Fact]
    public void Rows_wrapped_across_lines_are_put_back_together()
    {
        var tables = TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(Pasted), "campaigns");

        var table = Assert.Single(tables);
        Assert.Equal(';', table.Reading.Delimiter);
        Assert.Equal(["Customer", "Email", "Date", "Subscription", "Country", "Type", "Email Status"], table.Columns);
        Assert.Equal(4, table.RowCount);
        Assert.Equal("08/28/2026 09:29:36", table.Rows[0][2]);
        Assert.Equal("Курси роботи з Windows", table.Rows[1][3]);
        Assert.Equal("Sent", table.Rows[2][6]?.Trim());
        Assert.All(table.Rows, row => Assert.Equal(7, row.Cells.Count));
    }

    [Fact]
    public void A_row_that_is_short_because_its_last_cells_are_missing_stays_short()
    {
        const string csv = "name,city,cost\nA,Lviv\nB,Kyiv,600\n";

        var table = Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(csv), "short"));

        Assert.Equal(2, table.RowCount);
        Assert.Equal(2, table.Rows[0].Cells.Count);
        Assert.Equal(3, table.Rows[1].Cells.Count);
    }

    [Fact]
    public void A_table_in_the_message_is_split_from_what_was_said()
    {
        Assert.True(PastedTables.TrySplit("Here are the campaigns:\n" + Pasted, out var said, out var table));
        Assert.Equal("Here are the campaigns:", said);
        Assert.StartsWith("Customer;Email;Date;", table);
        Assert.Equal(8, table.Split('\n').Length);

        Assert.True(PastedTables.TrySplit(Pasted, out said, out _));
        Assert.Equal("", said);

        Assert.False(PastedTables.TrySplit("I want to save these — the conference in Lviv on 14 March 2025, 12 000 hryvnia, and the one in Kyiv on 20 May 2025, 8 500.", out _, out _));
        Assert.False(PastedTables.TrySplit("one line, with a comma", out _, out _));
        Assert.False(PastedTables.TrySplit("", out _, out _));
    }
}
