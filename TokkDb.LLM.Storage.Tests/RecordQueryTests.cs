using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage.Tests;

public sealed class RecordQueryTests
{
    private static readonly RecordQueryBinder Binder = new();

    /// <summary>Binds the tool query, then lets storage validate and run it.</summary>
    private static StorageQueryResult Execute(MemoryStorage storage, RecordQuery query) =>
        storage.ExecuteQuery(Binder.Bind(storage, query));

    private static IReadOnlyList<StorageValidationError> ErrorsFrom(MemoryStorage storage, RecordQuery query)
    {
        var thrown = Assert.Throws<StorageValidationException>(() => Execute(storage, query));
        return thrown.Errors.ToArray();
    }

    /// <summary>
    /// Customer -> Order -> Product, wired with declared relations so traversal
    /// is possible in both directions.
    /// </summary>
    private static MemoryStorage BuildShop()
    {
        var storage = new MemoryStorage();

        storage.CreateCollection(new CollectionDefinition("Customer", "Customers", new[]
        {
            new ColumnDefinition("CustomerId", ColumnType.String, unique: true),
            new ColumnDefinition("FullName", ColumnType.String),
            new ColumnDefinition("Phone", ColumnType.String),
            new ColumnDefinition("Age", ColumnType.Int32)
        }));

        storage.CreateCollection(new CollectionDefinition("Order", "Orders", new[]
        {
            new ColumnDefinition("OrderId", ColumnType.String, unique: true),
            new ColumnDefinition("CustomerId", ColumnType.String),
            new ColumnDefinition("Sku", ColumnType.String)
        }));

        storage.CreateCollection(new CollectionDefinition("Product", "Products", new[]
        {
            new ColumnDefinition("Sku", ColumnType.String, unique: true),
            new ColumnDefinition("Name", ColumnType.String),
            new ColumnDefinition("Price", ColumnType.Decimal)
        }));

        void Customer(string id, string name, string phone, int age) =>
            storage.Create("Customer", new Dictionary<string, object?>
            {
                ["CustomerId"] = id, ["FullName"] = name, ["Phone"] = phone, ["Age"] = age
            });

        Customer("c1", "Olena", "+380671112233", 30);
        Customer("c2", "John", "+14155550123", 45);
        Customer("c3", "Andriy", "+380509998877", 25);
        Customer("c4", "Marta", "+380631234567", 51);

        void Order(string id, string customer, string sku) =>
            storage.Create("Order", new Dictionary<string, object?>
            {
                ["OrderId"] = id, ["CustomerId"] = customer, ["Sku"] = sku
            });

        Order("o1", "c1", "p-cheap");
        Order("o2", "c2", "p-mid");
        Order("o3", "c3", "p-expensive");
        Order("o4", "c4", "p-cheap");

        void Product(string sku, string name, decimal price) =>
            storage.Create("Product", new Dictionary<string, object?>
            {
                ["Sku"] = sku, ["Name"] = name, ["Price"] = price
            });

        Product("p-cheap", "Mug", 12m);
        Product("p-mid", "Keyboard", 40m);
        Product("p-expensive", "Monitor", 250m);

        // Declared last: relation validation checks existing records, so the
        // data has to be complete before the relations exist.
        storage.AddRelation(new RelationDefinition(
            "CustomerOrders", RelationType.OneToMany, "Customer", "CustomerId", "Order", "CustomerId"));
        storage.AddRelation(new RelationDefinition(
            "OrderProduct", RelationType.ManyToOne, "Order", "Sku", "Product", "Sku"));

        return storage;
    }

    private static IReadOnlyList<string> Run(MemoryStorage storage, RecordQuery query, string nameColumn = "FullName")
    {
        return Execute(storage, query)
            .Rows.Select(row => RecordValueFormatter.Format(row.Fields[nameColumn]))
            .ToArray();
    }

    // =====================================================================
    // The two worked examples
    // =====================================================================

    [Fact]
    public void FindsCustomersWithUkrainianPhoneNumbers()
    {
        var storage = BuildShop();

        var names = Run(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" },
            OrderBy = [new RecordQuerySort { Column = "FullName", Direction = "asc" }]
        });

        Assert.Equal(new[] { "Andriy", "Marta", "Olena" }, names);
    }

    [Fact]
    public void FindsCustomersWhoBoughtAProductCostingFortyOrMore()
    {
        var storage = BuildShop();

        var names = Run(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter
            {
                Relation = "CustomerOrders",
                Quantifier = "any",
                Where = new RecordFilter
                {
                    Relation = "OrderProduct",
                    Quantifier = "any",
                    Where = new RecordFilter { Field = "Price", Operator = "gte", Value = "40" }
                }
            },
            OrderBy = [new RecordQuerySort { Column = "FullName" }]
        });

        // John bought the 40 keyboard (inclusive), Andriy the 250 monitor.
        Assert.Equal(new[] { "Andriy", "John" }, names);
    }

    // =====================================================================
    // Filtering, sorting, paging
    // =====================================================================

    [Fact]
    public void CombinesConditionsWithAndOr()
    {
        var storage = BuildShop();

        var names = Run(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter
            {
                Logic = "and",
                Filters =
                [
                    new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" },
                    new RecordFilter { Field = "Age", Operator = "gte", Value = "30" }
                ]
            },
            OrderBy = [new RecordQuerySort { Column = "Age", Direction = "desc" }]
        });

        Assert.Equal(new[] { "Marta", "Olena" }, names);
    }

    [Fact]
    public void NegatesWithNot()
    {
        var storage = BuildShop();

        var names = Run(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter
            {
                Logic = "not",
                Filters = [new RecordFilter { Field = "Phone", Operator = "startsWith", Value = "+380" }]
            }
        });

        Assert.Equal(new[] { "John" }, names);
    }

    [Fact]
    public void SupportsBetweenAndIn()
    {
        var storage = BuildShop();

        Assert.Equal(
            new[] { "Olena", "John" },
            Run(storage, new RecordQuery
            {
                CollectionName = "Customer",
                Where = new RecordFilter { Field = "Age", Operator = "between", Values = ["30", "45"] },
                OrderBy = [new RecordQuerySort { Column = "Age" }]
            }));

        Assert.Equal(
            new[] { "John", "Olena" },
            Run(storage, new RecordQuery
            {
                CollectionName = "Customer",
                Where = new RecordFilter { Field = "FullName", Operator = "in", Values = ["Olena", "John"] },
                OrderBy = [new RecordQuerySort { Column = "FullName" }]
            }));
    }

    [Fact]
    public void SkipAndTakeSliceTheOrderedResult()
    {
        var storage = BuildShop();

        var query = new RecordQuery
        {
            CollectionName = "Customer",
            OrderBy = [new RecordQuerySort { Column = "Age", Direction = "asc" }],
            Skip = 1,
            Take = 2
        };

        Assert.Equal(new[] { "Olena", "John" }, Run(storage, query));
    }

    [Fact]
    public void AnUnfilteredQueryIsCappedAtTenRecords()
    {
        // Its own collection, with no relations: records are added here after
        // the schema exists, which referential integrity would otherwise refuse.
        var storage = new MemoryStorage();
        storage.CreateCollection(new CollectionDefinition("Note", "Notes", new[]
        {
            new ColumnDefinition("Title", ColumnType.String)
        }));

        for (var i = 0; i < 25; i++)
        {
            storage.Create("Note", new Dictionary<string, object?> { ["Title"] = $"Note {i}" });
        }

        // Reading a collection with no condition is legitimate, but it must not
        // hand back everything the collection holds.
        var defaulted = Execute(storage, new RecordQuery { CollectionName = "Note" });
        Assert.Equal(RecordQueryBinder.DefaultTake, defaulted.Take);
        Assert.Equal(10, defaulted.Rows.Count);

        // The cap is a default, not a limit: asking for more still works.
        Assert.Equal(25, Execute(storage, new RecordQuery
        {
            CollectionName = "Note", Take = 100
        }).Rows.Count);
    }

    [Fact]
    public void RecordIdsFetchParticularRecords()
    {
        var storage = BuildShop();

        var all = Execute(storage, new RecordQuery
        {
            CollectionName = "Customer",
            OrderBy = [new RecordQuerySort { Column = "FullName" }]
        }).Rows;

        var wanted = all.Take(2).Select(row => row.Id.ToString()).ToList();

        Assert.Equal(
            new[] { "Andriy", "John" },
            Run(storage, new RecordQuery
            {
                CollectionName = "Customer",
                RecordIds = wanted,
                OrderBy = [new RecordQuerySort { Column = "FullName" }]
            }));
    }

    [Fact]
    public void RecordIdsNarrowTheSearchRatherThanReplacingIt()
    {
        var storage = BuildShop();

        var ids = Execute(storage, new RecordQuery { CollectionName = "Customer" })
            .Rows.Select(row => row.Id.ToString())
            .ToList();

        // Every customer by id, but only those over 40 asked for.
        Assert.Equal(
            new[] { "John", "Marta" },
            Run(storage, new RecordQuery
            {
                CollectionName = "Customer",
                RecordIds = ids,
                Where = new RecordFilter { Field = "Age", Operator = "gt", Value = "40" },
                OrderBy = [new RecordQuerySort { Column = "FullName" }]
            }));

        // An id that belongs to no record simply matches nothing.
        Assert.Empty(Execute(storage, new RecordQuery
        {
            CollectionName = "Customer",
            RecordIds = [Guid.NewGuid().ToString()]
        }).Rows);
    }

    [Fact]
    public void AMalformedRecordIdIsReportedRatherThanIgnored()
    {
        var storage = BuildShop();

        var errors = ErrorsFrom(storage, new RecordQuery
        {
            CollectionName = "Customer",
            RecordIds = ["not-an-id"]
        });

        Assert.Contains(errors, error => error.Code == "InvalidRecordId");
    }

    [Fact]
    public void SortsNumericallyNotAlphabetically()
    {
        var storage = BuildShop();

        var prices = Run(
            storage,
            new RecordQuery
            {
                CollectionName = "Product",
                OrderBy = [new RecordQuerySort { Column = "Price", Direction = "asc" }]
            },
            nameColumn: "Price");

        Assert.Equal(new[] { "12", "40", "250" }, prices);
    }

    [Fact]
    public void SelectReturnsOnlyRequestedColumns()
    {
        var storage = BuildShop();
        var row = Execute(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Select = ["FullName"],
            Take = 1
        }).Rows[0];

        Assert.Equal(["FullName"], row.Fields.Keys.ToArray());
        Assert.NotEqual(Guid.Empty, row.Id);
    }

    [Fact]
    public void RelationQuantifiersNoneAndAllAreSupported()
    {
        var storage = BuildShop();

        // Customers with no order for a product costing 40 or more.
        var none = Run(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter
            {
                Relation = "CustomerOrders",
                Quantifier = "none",
                Where = new RecordFilter
                {
                    Relation = "OrderProduct",
                    Quantifier = "any",
                    Where = new RecordFilter { Field = "Price", Operator = "gte", Value = "40" }
                }
            },
            OrderBy = [new RecordQuerySort { Column = "FullName" }]
        });

        Assert.Equal(new[] { "Marta", "Olena" }, none);
    }

    [Fact]
    public void TraversesARelationDeclaredInTheOppositeDirection()
    {
        var storage = BuildShop();

        // Order -> Customer, though the relation is declared Customer -> Order.
        var rows = Execute(storage, new RecordQuery
        {
            CollectionName = "Order",
            Where = new RecordFilter
            {
                Relation = "CustomerOrders",
                Quantifier = "any",
                Where = new RecordFilter { Field = "FullName", Operator = "eq", Value = "Olena" }
            }
        }).Rows;

        Assert.Single(rows);
        Assert.Equal("o1", rows[0].Fields["OrderId"]);
    }

    // =====================================================================
    // Validation - the barrier between the model and storage
    // =====================================================================

    [Fact]
    public void UnknownCollectionColumnAndRelationAreRejected()
    {
        var storage = BuildShop();

        Assert.Contains(
            ErrorsFrom(storage, new RecordQuery { CollectionName = "Nope" }),
            error => error.Code == "CollectionNotFound");

        // Binding stage: an unresolved name leaves no definition to bind.
        Assert.Contains(
            ErrorsFrom(storage, new RecordQuery
            {
                CollectionName = "Customer",
                Where = new RecordFilter { Field = "Nope", Operator = "eq", Value = "x" }
            }),
            error => error.Code == "ColumnNotFound" && error.Message.Contains("Available columns"));

        Assert.Contains(
            ErrorsFrom(storage, new RecordQuery
            {
                CollectionName = "Customer",
                Where = new RecordFilter { Relation = "Nope", Quantifier = "any" }
            }),
            error => error.Code == "RelationNotFound");
    }

    [Fact]
    public void OperatorMustSuitTheColumnType()
    {
        var storage = BuildShop();

        // Storage stage: the column resolves, but the operator does not suit its type.
        Assert.Contains(
            ErrorsFrom(storage, new RecordQuery
            {
                CollectionName = "Product",
                Where = new RecordFilter { Field = "Price", Operator = "contains", Value = "4" }
            }),
            error => error.Code == "OperatorNotAllowed" && error.ColumnName == "Price");
    }

    [Fact]
    public void OperandMustCoerceToTheColumnType()
    {
        var storage = BuildShop();

        // Storage stage: operands are converted using the column definition.
        Assert.Contains(
            ErrorsFrom(storage, new RecordQuery
            {
                CollectionName = "Product",
                Where = new RecordFilter { Field = "Price", Operator = "gte", Value = "expensive" }
            }),
            error => error.Code == "InvalidOperandType" && error.Message.Contains("not a valid Decimal"));
    }

    [Fact]
    public void NestedColumnIsResolvedAgainstTheRelatedCollection()
    {
        var storage = BuildShop();

        // FullName exists on Customer but not on Product, so this must fail.
        Assert.Contains(
            ErrorsFrom(storage, new RecordQuery
            {
                CollectionName = "Order",
                Where = new RecordFilter
                {
                    Relation = "OrderProduct",
                    Quantifier = "any",
                    Where = new RecordFilter { Field = "FullName", Operator = "eq", Value = "Olena" }
                }
            }),
            error => error.Code == "ColumnNotFound" && error.Message.Contains("collection 'Product'"));
    }

    [Fact]
    public void RelationThatDoesNotTouchTheScopeIsRejected()
    {
        var storage = BuildShop();

        Assert.Contains(
            ErrorsFrom(storage, new RecordQuery
            {
                CollectionName = "Customer",
                Where = new RecordFilter { Relation = "OrderProduct", Quantifier = "any" }
            }),
            error => error.Code == "RelationNotApplicable");
    }

    [Fact]
    public void MalformedFilterShapesAreRejected()
    {
        var storage = BuildShop();

        // No shape at all.
        Assert.Throws<StorageValidationException>(() => Execute(storage, new RecordQuery {
            CollectionName = "Customer", Where = new RecordFilter()
        }));

        // Two shapes at once.
        Assert.Throws<StorageValidationException>(() => Execute(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "Age", Operator = "eq", Value = "1", Logic = "and" }
        }));

        // between with the wrong operand count.
        Assert.Throws<StorageValidationException>(() => Execute(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "Age", Operator = "between", Values = ["1"] }
        }));
    }

    [Fact]
    public void LimitsGuardAgainstPathologicalQueries()
    {
        var storage = BuildShop();

        Assert.Throws<StorageValidationException>(() => Execute(storage, new RecordQuery {
            CollectionName = "Customer", Take = RecordQueryBinder.MaxTake + 1
        }));

        Assert.Throws<StorageValidationException>(() => Execute(storage, new RecordQuery {
            CollectionName = "Customer", Skip = -1
        }));

        // Nesting beyond the allowed depth.
        var deep = new RecordFilter { Field = "Age", Operator = "eq", Value = "1" };
        for (var i = 0; i < RecordQueryBinder.MaxFilterDepth + 1; i++)
        {
            deep = new RecordFilter { Logic = "and", Filters = [deep] };
        }

        Assert.Throws<StorageValidationException>(() => Execute(storage, new RecordQuery {
            CollectionName = "Customer", Where = deep
        }));
    }

    [Fact]
    public void SemanticNormalisationIsAppliedToOperands()
    {
        // A phone column whose stored values are normalised by removing spaces
        // must match an operand written with spaces.
        var registry = new SemanticTypeRegistry();
        registry.Register(new SemanticTypeDefinition(
            "phone", "Phone", "Phone number", ColumnType.String,
            NormalizationRules: ["Trim", "ToLowerInvariant"]));

        var storage = new MemoryStorage(registry);
        storage.CreateCollection(new CollectionDefinition("Person", columns: new[]
        {
            new ColumnDefinition("Email", ColumnType.String, semanticTypeName: "phone")
        }));
        storage.Create("Person", new Dictionary<string, object?> { ["Email"] = "USER@EXAMPLE.COM" });

        Assert.Single(Execute(storage, new RecordQuery
        {
            CollectionName = "Person",
            // Operand in a different case: normalisation must align it.
            Where = new RecordFilter { Field = "Email", Operator = "eq", Value = "  User@Example.com  " }
        }).Rows);
    }

    [Fact]
    public void EmptyResultIsNotAnError()
    {
        var storage = BuildShop();

        Assert.Empty(Execute(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter { Field = "FullName", Operator = "eq", Value = "Nobody" }
        }).Rows);
    }

    [Fact]
    public void StorageRejectsAColumnBorrowedFromAnotherCollection()
    {
        var storage = BuildShop();

        // Built by hand rather than through the binder: the column definition is
        // real, but it belongs to Product, not Customer. Storage validates the
        // definitions against the collection under filter, so this is caught
        // even though nothing was resolved by name.
        var customer = storage.GetCollectionDefinition("Customer")!;
        var priceColumn = storage.GetCollectionDefinition("Product")!
            .Columns.First(column => column.Name == "Price");

        var query = new StorageQuery(
            customer,
            new StorageFieldFilter(priceColumn, QueryOperator.GreaterOrEqual, ["40"]),
            Array.Empty<StorageSort>(),
            0,
            50,
            Array.Empty<ColumnDefinition>());

        var thrown = Assert.Throws<StorageValidationException>(() => storage.ExecuteQuery(query));
        Assert.Contains(thrown.Errors, error => error.Code == "ColumnNotInCollection");
    }

    [Fact]
    public void BinderProducesDefinitionsRatherThanNames()
    {
        var storage = BuildShop();

        var bound = Binder.Bind(storage, new RecordQuery
        {
            CollectionName = "Customer",
            Where = new RecordFilter
            {
                Relation = "CustomerOrders",
                Quantifier = "any",
                Where = new RecordFilter { Field = "Sku", Operator = "eq", Value = "p-cheap" }
            },
            OrderBy = [new RecordQuerySort { Column = "FullName" }]
        });

        Assert.Equal("Customer", bound.Collection.Name);
        var relation = Assert.IsType<StorageRelationFilter>(bound.Where);
        Assert.Equal("CustomerOrders", relation.Relation.Name);
        Assert.Equal("Order", relation.TargetCollection.Name);

        // Every reference is a resolved definition carrying its declared type.
        Assert.Equal(ColumnType.String, relation.SourceColumn.Type);
        var inner = Assert.IsType<StorageFieldFilter>(relation.Inner);
        Assert.Equal("Sku", inner.Column.Name);
        Assert.Equal("FullName", bound.OrderBy[0].Column.Name);
    }
}
