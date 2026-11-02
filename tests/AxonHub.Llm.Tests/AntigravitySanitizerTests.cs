using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/antigravity/sanitizer_test.go and
/// sanitizer_uppercase_test.go: JSON schema sanitization cases (checked with
/// the reference's containment semantics) and the UPPERCASE type conversion.
/// </summary>
public sealed class AntigravitySanitizerTests
{
    // ------------------------------------------------------------------
    // SanitizeJSONSchema (reference TestSanitizeJSONSchema)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(
        "Convert $ref to hints",
        """
        {
            "type": "object",
            "properties": {
                "user": { "$ref": "#/$defs/User" }
            }
        }
        """,
        """
        {
            "type": "object",
            "properties": {
                "user": {
                    "type": "object",
                    "description": "See: User"
                }
            }
        }
        """)]
    [InlineData(
        "Convert const to enum",
        """
        {
            "type": "object",
            "properties": {
                "mode": { "const": "json" }
            }
        }
        """,
        """
        {
            "type": "object",
            "properties": {
                "mode": { "enum": ["json"] }
            }
        }
        """)]
    [InlineData(
        "Add enum hints",
        """
        {
            "type": "string",
            "enum": ["a", "b"]
        }
        """,
        """
        {
            "type": "string",
            "enum": ["a", "b"],
            "description": "Allowed: a, b"
        }
        """)]
    [InlineData(
        "Add additionalProperties hints",
        """
        {
            "type": "object",
            "additionalProperties": false
        }
        """,
        """
        {
            "type": "object",
            "description": "No extra properties allowed",
            "properties": {
                "_placeholder": {
                    "type": "boolean",
                    "description": "Placeholder. Always pass true."
                }
            },
            "required": ["_placeholder"]
        }
        """)]
    [InlineData(
        "Move constraints to description",
        """
        {
            "type": "string",
            "minLength": 5,
            "maxLength": 10
        }
        """,
        """
        {
            "type": "string",
            "description": "minLength: 5 (maxLength: 10)"
        }
        """)]
    [InlineData(
        "Remove constraint keywords from nested properties",
        """
        {
            "type": "object",
            "properties": {
                "max_turns": {
                    "type": "integer",
                    "description": "Maximum number of agentic turns",
                    "exclusiveMinimum": 0
                }
            }
        }
        """,
        """
        {
            "type": "object",
            "properties": {
                "max_turns": {
                    "type": "integer",
                    "description": "Maximum number of agentic turns (exclusiveMinimum: 0)"
                }
            }
        }
        """)]
    [InlineData(
        "Merge allOf",
        """
        {
            "allOf": [
                { "properties": { "a": { "type": "string" } }, "required": ["a"] },
                { "properties": { "b": { "type": "integer" } } }
            ]
        }
        """,
        """
        {
            "properties": {
                "a": { "type": "string" },
                "b": { "type": "integer" }
            },
            "required": ["a"]
        }
        """)]
    [InlineData(
        "Flatten anyOf",
        """
        {
            "anyOf": [
                { "type": "string" },
                { "type": "integer" }
            ]
        }
        """,
        """
        {
            "type": "string",
            "description": "Accepts: string | integer"
        }
        """)]
    [InlineData(
        "Flatten nullable type array",
        """
        {
            "type": ["string", "null"]
        }
        """,
        """
        {
            "type": "string",
            "description": "nullable"
        }
        """)]
    [InlineData(
        "Add placeholder for empty object",
        """
        {
            "type": "object",
            "properties": {}
        }
        """,
        """
        {
            "type": "object",
            "properties": {
                "_placeholder": {
                    "type": "boolean",
                    "description": "Placeholder. Always pass true."
                }
            },
            "required": ["_placeholder"]
        }
        """)]
    public void TestSanitizeJSONSchema(string name, string input, string expected)
    {
        _ = name;
        var inputSchema = AntigravitySanitizer.ParseSchema(input);
        var expectedSchema = AntigravitySanitizer.ParseSchema(expected);

        Assert.NotNull(inputSchema);
        Assert.NotNull(expectedSchema);

        var sanitized = AntigravitySanitizer.SanitizeJSONSchema(inputSchema);

        CheckExpected(expectedSchema, sanitized);
    }

    /// <summary>
    /// Port of checkExpected (sanitizer_test.go:204-246): expected map keys
    /// must be present in the actual tree with recursively equal values
    /// (containment, since sanitization may keep extra fields); descriptions
    /// and other leaves compare exactly (assert.Equal in the reference).
    /// </summary>
    private static void CheckExpected(object? expected, object? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        if (expected is Dictionary<string, object?> expectedMap
            && actual is Dictionary<string, object?> actualMap)
        {
            foreach (var (key, value) in expectedMap)
            {
                Assert.True(actualMap.ContainsKey(key));
                CheckExpected(value, actualMap[key]);
            }

            return;
        }

        if (expected is List<object?> expectedList && actual is List<object?> actualList)
        {
            Assert.Equal(expectedList.Count, actualList.Count);

            for (var i = 0; i < expectedList.Count; i++)
            {
                CheckExpected(expectedList[i], actualList[i]);
            }

            return;
        }

        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------
    // UppercaseSchemaTypes (reference TestUppercaseSchemaTypes)
    // ------------------------------------------------------------------

    [Fact]
    public void TestUppercaseSchemaTypes_UppercasesSimpleType()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "string",
        };

        var result = AntigravitySanitizer.UppercaseSchemaTypes(input);

        Assert.NotNull(result);
        Assert.Equal("STRING", result["type"]);
    }

    [Fact]
    public void TestUppercaseSchemaTypes_UppercasesObjectWithProperties()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "string" },
                ["age"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "integer" },
            },
        };

        var result = AntigravitySanitizer.UppercaseSchemaTypes(input);

        Assert.NotNull(result);
        Assert.Equal("OBJECT", result["type"]);

        var props = Assert.IsType<Dictionary<string, object?>>(result["properties"]);
        var nameSchema = Assert.IsType<Dictionary<string, object?>>(props["name"]);
        Assert.Equal("STRING", nameSchema["type"]);

        var ageSchema = Assert.IsType<Dictionary<string, object?>>(props["age"]);
        Assert.Equal("INTEGER", ageSchema["type"]);
    }

    [Fact]
    public void TestUppercaseSchemaTypes_UppercasesArrayTypes()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "array",
            ["items"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "string" },
        };

        var result = AntigravitySanitizer.UppercaseSchemaTypes(input);

        Assert.NotNull(result);
        Assert.Equal("ARRAY", result["type"]);

        var items = Assert.IsType<Dictionary<string, object?>>(result["items"]);
        Assert.Equal("STRING", items["type"]);
    }

    [Fact]
    public void TestUppercaseSchemaTypes_UppercasesUnionTypesAnyOf()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["anyOf"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "string" },
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "number" },
            },
        };

        var result = AntigravitySanitizer.UppercaseSchemaTypes(input);

        Assert.NotNull(result);
        var anyOf = Assert.IsType<List<object?>>(result["anyOf"]);
        Assert.Equal("STRING", Assert.IsType<Dictionary<string, object?>>(anyOf[0])["type"]);
        Assert.Equal("NUMBER", Assert.IsType<Dictionary<string, object?>>(anyOf[1])["type"]);
    }

    [Fact]
    public void TestUppercaseSchemaTypes_PreservesNonTypeFields()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "string",
            ["description"] = "A string field",
            ["minLength"] = 1.0,
            ["maxLength"] = 100.0,
        };

        var result = AntigravitySanitizer.UppercaseSchemaTypes(input);

        Assert.NotNull(result);
        Assert.Equal("STRING", result["type"]);
        Assert.Equal("A string field", result["description"]);
        Assert.Equal(1.0, result["minLength"]);
        Assert.Equal(100.0, result["maxLength"]);
    }

    [Fact]
    public void TestUppercaseSchemaTypes_HandlesNestedObjects()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["address"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["street"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "string" },
                        ["zipCode"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "integer" },
                    },
                },
            },
        };

        var result = AntigravitySanitizer.UppercaseSchemaTypes(input);

        Assert.NotNull(result);
        Assert.Equal("OBJECT", result["type"]);

        var props = Assert.IsType<Dictionary<string, object?>>(result["properties"]);
        var addressSchema = Assert.IsType<Dictionary<string, object?>>(props["address"]);
        Assert.Equal("OBJECT", addressSchema["type"]);

        var addressProps = Assert.IsType<Dictionary<string, object?>>(addressSchema["properties"]);
        var streetSchema = Assert.IsType<Dictionary<string, object?>>(addressProps["street"]);
        Assert.Equal("STRING", streetSchema["type"]);

        var zipSchema = Assert.IsType<Dictionary<string, object?>>(addressProps["zipCode"]);
        Assert.Equal("INTEGER", zipSchema["type"]);
    }

    [Fact]
    public void TestUppercaseSchemaTypes_HandlesNilSchema()
        => Assert.Null(AntigravitySanitizer.UppercaseSchemaTypes(null));

    [Fact]
    public void TestUppercaseSchemaTypes_HandlesEmptySchema()
    {
        var result = AntigravitySanitizer.UppercaseSchemaTypes(
            new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void TestUppercaseSchemaTypes_HandlesAllOf()
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["allOf"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "string" },
                    },
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["age"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "integer" },
                    },
                },
            },
        };

        var result = AntigravitySanitizer.UppercaseSchemaTypes(input);

        Assert.NotNull(result);
        var allOf = Assert.IsType<List<object?>>(result["allOf"]);
        Assert.Equal("OBJECT", Assert.IsType<Dictionary<string, object?>>(allOf[0])["type"]);
        Assert.Equal("OBJECT", Assert.IsType<Dictionary<string, object?>>(allOf[1])["type"]);
    }
}
