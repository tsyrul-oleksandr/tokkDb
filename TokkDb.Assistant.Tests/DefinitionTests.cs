using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The parts of SC-3 and SC-4 that the definitions answer on their own, before any
/// implementation of <see cref="IStorage"/> exists. The answers that need a storage to
/// demonstrate - identity, uniqueness, validation at the write, GetAll's silence about order -
/// are the contract suite's, in 1.2.
/// </summary>
public sealed class DefinitionTests
{
    // ---- SC-4, the naming rule --------------------------------------------------------------

    [Theory]
    [InlineData("  expenses  ", "expenses")]
    [InlineData("expenses\n", "expenses")]
    [InlineData("expenses", "expenses")]
    public void A_name_is_trimmed_when_it_is_created(string given, string expected)
    {
        Assert.Equal(expected, new CollectionDefinition(given).Name);
        Assert.Equal(expected, new ColumnDefinition(given, ColumnType.Text).Name);
    }

    [Fact]
    public void Names_are_compared_ordinally_so_two_spellings_are_two_names()
    {
        var definition = new CollectionDefinition(
            "expenses",
            columns: [new ColumnDefinition("amount", ColumnType.Decimal)]);

        Assert.NotNull(definition.Column("amount"));
        Assert.Null(definition.Column("Amount"));
        Assert.Null(definition.Column("AMOUNT"));
    }

    /// <summary>
    /// The consequence of comparing ordinally, stated so that it is a decision and not a
    /// surprise. Collapsing these two would mean the storage deciding that two spellings are one
    /// column, which is the guess SC-4's answer exists to refuse.
    /// </summary>
    [Fact]
    public void Two_columns_differing_only_in_case_are_two_columns()
    {
        var definition = new CollectionDefinition(
            "expenses",
            columns:
            [
                new ColumnDefinition("amount", ColumnType.Decimal),
                new ColumnDefinition("Amount", ColumnType.Text)
            ]);

        Assert.Equal(2, definition.Columns.Count);
        Assert.Equal(ColumnType.Decimal, definition.Column("amount")!.Type);
        Assert.Equal(ColumnType.Text, definition.Column("Amount")!.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9lives")]
    [InlineData("has space")]
    [InlineData("has-hyphen")]
    [InlineData("_leading")]
    public void A_name_that_is_not_a_name_is_refused_and_says_which_name_it_was(string name)
    {
        var thrown = Assert.Throws<InvalidDefinitionException>(() => new CollectionDefinition(name));
        Assert.Equal("collection name", thrown.Member);

        var onColumn = Assert.Throws<InvalidDefinitionException>(() => new ColumnDefinition(name, ColumnType.Text));
        Assert.Equal("column name", onColumn.Member);
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        var thrown = Assert.Throws<InvalidDefinitionException>(
            () => new CollectionDefinition("a" + new string('b', 64)));

        Assert.Equal("collection name", thrown.Member);
    }

    [Fact]
    public void Two_columns_with_one_name_are_refused()
    {
        var thrown = Assert.Throws<InvalidDefinitionException>(() => new CollectionDefinition(
            "expenses",
            columns:
            [
                new ColumnDefinition("amount", ColumnType.Decimal),
                new ColumnDefinition("amount", ColumnType.Text)
            ]));

        Assert.Equal("column name", thrown.Member);
    }

    // ---- SC-3, what each type accepts ------------------------------------------------------

    [Theory]
    // Text takes text and nothing else.
    [InlineData(ColumnType.Text, "conference", true)]
    [InlineData(ColumnType.Text, 840, false)]
    [InlineData(ColumnType.Text, true, false)]
    // Every integer that fits widens; nothing that has to be parsed or rounded does.
    [InlineData(ColumnType.Integer, 840, true)]
    [InlineData(ColumnType.Integer, 840L, true)]
    [InlineData(ColumnType.Integer, (short)840, true)]
    [InlineData(ColumnType.Integer, (byte)8, true)]
    [InlineData(ColumnType.Integer, "840", false)]
    [InlineData(ColumnType.Integer, 840.0, false)]
    [InlineData(ColumnType.Integer, 840.5, false)]
    // A decimal column takes whole numbers too, because 840 EUR is a decimal amount.
    [InlineData(ColumnType.Decimal, 840, true)]
    [InlineData(ColumnType.Decimal, "840.00", false)]
    [InlineData(ColumnType.Decimal, 840.5, false)]
    [InlineData(ColumnType.Boolean, true, true)]
    [InlineData(ColumnType.Boolean, 1, false)]
    [InlineData(ColumnType.Boolean, "true", false)]
    public void A_value_is_accepted_only_if_it_is_a_value_of_the_column_type(
        ColumnType type,
        object value,
        bool accepted)
    {
        Assert.Equal(accepted, ColumnTypes.TryCanonicalise(type, value, out _));
    }

    [Fact]
    public void A_decimal_column_takes_a_decimal_and_hands_back_a_decimal()
    {
        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Decimal, 840.50m, out var canonical));
        Assert.Equal(840.50m, canonical);
        Assert.IsType<decimal>(canonical);
    }

    [Fact]
    public void An_integer_offered_to_an_integer_column_is_stored_as_a_long()
    {
        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Integer, 840, out var canonical));
        Assert.Equal(840L, canonical);
        Assert.IsType<long>(canonical);
    }

    [Fact]
    public void A_whole_number_offered_to_a_decimal_column_is_stored_as_a_decimal()
    {
        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Decimal, 840, out var canonical));
        Assert.Equal(840m, canonical);
        Assert.IsType<decimal>(canonical);
    }

    [Fact]
    public void A_ulong_too_large_for_a_long_is_refused_rather_than_wrapped()
    {
        Assert.False(ColumnTypes.TryCanonicalise(ColumnType.Integer, ulong.MaxValue, out _));
        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Integer, (ulong)840, out var small));
        Assert.Equal(840L, small);
    }

    [Fact]
    public void A_date_column_takes_a_date_and_refuses_a_date_and_time()
    {
        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Date, new DateOnly(2026, 7, 20), out var date));
        Assert.Equal(new DateOnly(2026, 7, 20), date);

        Assert.False(ColumnTypes.TryCanonicalise(ColumnType.Date, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), out _));
    }

    [Fact]
    public void A_timestamp_column_holds_UTC_and_refuses_a_time_with_no_zone()
    {
        var utc = new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Utc);

        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Timestamp, utc, out var kept));
        Assert.Equal(utc, kept);

        var local = new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Local);
        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Timestamp, local, out var converted));
        Assert.Equal(local.ToUniversalTime(), converted);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)converted!).Kind);

        var offset = new DateTimeOffset(2026, 7, 20, 12, 30, 0, TimeSpan.FromHours(3));
        Assert.True(ColumnTypes.TryCanonicalise(ColumnType.Timestamp, offset, out var flattened));
        Assert.Equal(utc, flattened);

        Assert.False(ColumnTypes.TryCanonicalise(
            ColumnType.Timestamp,
            new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Unspecified),
            out _));
    }

    [Fact]
    public void Nothing_is_a_value_of_every_type_because_absence_has_no_type()
    {
        foreach (var type in Enum.GetValues<ColumnType>())
        {
            Assert.True(ColumnTypes.TryCanonicalise(type, null, out var canonical));
            Assert.Null(canonical);
        }
    }

    // ---- Defaults ---------------------------------------------------------------------------

    [Fact]
    public void A_default_of_the_wrong_type_is_refused_when_the_column_is_built()
    {
        var thrown = Assert.Throws<InvalidDefinitionException>(
            () => new ColumnDefinition("amount", ColumnType.Decimal, defaultValue: "nothing"));

        Assert.Equal("default value", thrown.Member);
    }

    [Fact]
    public void A_default_is_kept_as_the_type_the_column_hands_back()
    {
        var column = new ColumnDefinition("amount", ColumnType.Decimal, defaultValue: 0);
        Assert.Equal(0m, column.DefaultValue);
        Assert.IsType<decimal>(column.DefaultValue);
    }

    // ---- The display rule -------------------------------------------------------------------

    [Fact]
    public void A_display_rule_reports_the_columns_it_names_once_each_in_order()
    {
        var rule = new DisplayRule("{event} in {city} ({event})");
        Assert.Equal(["event", "city"], rule.ColumnReferences);
    }

    [Theory]
    [InlineData("{event")]
    [InlineData("event}")]
    [InlineData("{}")]
    [InlineData("{not a name}")]
    [InlineData("no columns at all")]
    public void A_display_rule_that_does_not_parse_is_refused(string template)
    {
        Assert.Throws<InvalidDefinitionException>(() => new DisplayRule(template));
        Assert.Null(DisplayRule.TryCreate(template));
    }

    [Fact]
    public void A_doubled_brace_is_a_literal_brace()
    {
        var rule = new DisplayRule("{{{event}}}");
        Assert.Equal(["event"], rule.ColumnReferences);
        Assert.Equal(3, rule.Segments.Count);
        Assert.Equal("{", rule.Segments[0].Text);
        Assert.True(rule.Segments[1].IsColumnReference);
        Assert.Equal("}", rule.Segments[2].Text);
    }

    [Fact]
    public void Renaming_a_column_rewrites_the_references_and_leaves_the_literal_text_alone()
    {
        var rule = new DisplayRule("event: {event}");
        var renamed = rule.WithColumnRenamed("event", "conference");

        Assert.Equal("event: {conference}", renamed.Template);
        Assert.Equal(["conference"], renamed.ColumnReferences);
    }

    // ---- Records ----------------------------------------------------------------------------

    [Fact]
    public void A_column_a_record_never_had_a_value_for_is_absent_rather_than_nothing()
    {
        var id = Ulid.NewUlid();
        var record = new StorageRecord(id, "expenses", new Dictionary<string, object?> { ["amount"] = 840m });

        Assert.True(record.Has("amount"));
        Assert.False(record.Has("city"));
        Assert.Null(record["city"]);

        var cleared = record.With("amount", null);
        Assert.True(cleared.Has("amount"));
        Assert.Null(cleared["amount"]);

        var removed = record.Without("amount");
        Assert.False(removed.Has("amount"));
    }

    [Fact]
    public void Changing_a_record_keeps_its_identity_and_its_collection()
    {
        var id = Ulid.NewUlid();
        var record = new StorageRecord(id, "expenses", new Dictionary<string, object?> { ["amount"] = 840m });
        var changed = record.With("amount", 900m);

        Assert.Equal(id, changed.Id);
        Assert.Equal("expenses", changed.CollectionName);
        Assert.Equal(900m, changed["amount"]);
        Assert.Equal(840m, record["amount"]);
    }

    // ---- Equality ---------------------------------------------------------------------------

    /// <summary>
    /// Not a numbered requirement, but the comparison the contract suite and "has anything about
    /// this collection changed?" both need. A record's synthesised equality would compare the
    /// column list by reference and answer no to both.
    /// </summary>
    [Fact]
    public void Two_definitions_that_say_the_same_thing_are_equal()
    {
        CollectionDefinition Build() => new(
            "expenses",
            "money I spent",
            columns:
            [
                new ColumnDefinition("event", ColumnType.Text, "what it was for"),
                new ColumnDefinition("amount_eur", ColumnType.Decimal, unique: false)
            ],
            metadata: new Dictionary<string, string?> { ["source"] = "a spreadsheet" },
            displayRule: new DisplayRule("{event}"));

        Assert.Equal(Build(), Build());
        Assert.Equal(Build().GetHashCode(), Build().GetHashCode());
    }

    [Fact]
    public void Definitions_that_differ_anywhere_are_not_equal()
    {
        var baseline = new CollectionDefinition(
            "expenses",
            "money I spent",
            columns: [new ColumnDefinition("event", ColumnType.Text)],
            metadata: new Dictionary<string, string?> { ["source"] = "a spreadsheet" },
            displayRule: new DisplayRule("{event}"));

        Assert.NotEqual(baseline, new CollectionDefinition("expenses", "something else",
            columns: baseline.Columns, metadata: baseline.Metadata, displayRule: baseline.DisplayRule));

        Assert.NotEqual(baseline, baseline.WithColumns(
            [new ColumnDefinition("event", ColumnType.Text), new ColumnDefinition("city", ColumnType.Text)]));

        Assert.NotEqual(baseline, baseline.WithColumns([new ColumnDefinition("event", ColumnType.Integer)]));

        Assert.NotEqual(baseline, baseline.WithMetadata(new Dictionary<string, string?>()));

        Assert.NotEqual(baseline, baseline.WithDisplayRule(null));

        Assert.NotEqual(baseline, baseline.WithDisplayRule(new DisplayRule("{event}!")));
    }

    [Fact]
    public void The_order_of_the_columns_is_part_of_the_definition()
    {
        var event_ = new ColumnDefinition("event", ColumnType.Text);
        var city = new ColumnDefinition("city", ColumnType.Text);

        Assert.NotEqual(
            new CollectionDefinition("expenses", columns: [event_, city]),
            new CollectionDefinition("expenses", columns: [city, event_]));
    }

    [Fact]
    public void Two_display_rules_from_the_same_template_are_equal()
    {
        Assert.Equal(new DisplayRule("{event} in {city}"), new DisplayRule("{event} in {city}"));
        Assert.NotEqual(new DisplayRule("{event}"), new DisplayRule("{city}"));
    }

    [Fact]
    public void Two_records_holding_the_same_values_are_equal()
    {
        var id = Ulid.NewUlid();

        StorageRecord Build() => new(id, "expenses", new Dictionary<string, object?>
        {
            ["event"] = "EuroPython",
            ["amount_eur"] = 840m
        });

        Assert.Equal(Build(), Build());
        Assert.Equal(Build().GetHashCode(), Build().GetHashCode());

        Assert.NotEqual(Build(), Build().With("amount_eur", 900m));
        Assert.NotEqual(Build(), Build().Without("event"));
        Assert.NotEqual(Build(), new StorageRecord(Ulid.NewUlid(), "expenses", Build().Fields));
    }

    /// <summary>
    /// A record with a column present and set to nothing is not a record without that column.
    /// The distinction is only worth having if equality respects it.
    /// </summary>
    [Fact]
    public void Set_to_nothing_and_not_set_are_not_equal()
    {
        var id = Ulid.NewUlid();
        var withColumn = new StorageRecord(id, "expenses", new Dictionary<string, object?> { ["city"] = null });
        var withoutColumn = new StorageRecord(id, "expenses", new Dictionary<string, object?>());

        Assert.NotEqual(withColumn, withoutColumn);
    }
}
