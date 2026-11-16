using AxonHub.Api.Diagnostics;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the <c>json.Indent</c> pass behind <c>json.MarshalIndent</c>
/// (encoding/json/indent.go): nested containers start on their own line, empty
/// containers stay inline, whitespace between tokens is dropped and string
/// literals pass through untouched.
/// </summary>
public sealed class GoJsonIndentTests
{
    [Fact]
    public void NestedContainersStartOnTheirOwnLine()
    {
        Assert.Equal(
            """
            {
              "channels": [
                {
                  "id": 1
                },
                {}
              ]
            }
            """.ReplaceLineEndings("\n"),
            GoJsonIndent.Format("""{"channels":[{"id":1},{}]}""", "  "));

        Assert.Equal(
            """
            {
              "b": [
                [],
                true
              ]
            }
            """.ReplaceLineEndings("\n"),
            GoJsonIndent.Format("""{"b":[[],true]}""", "  "));
    }

    [Fact]
    public void EmptyRootContainersStayInline()
    {
        Assert.Equal("{}", GoJsonIndent.Format("{}", "  "));
        Assert.Equal("[]", GoJsonIndent.Format("[]", "  "));
    }

    [Fact]
    public void StringLiteralsPassThroughUntouched()
    {
        Assert.Equal(
            """
            {
              "a": "x,y:{}[]\\\"",
              "b": 1
            }
            """.ReplaceLineEndings("\n"),
            GoJsonIndent.Format("""{"a":"x,y:{}[]\\\"","b":1}""", "  "));
    }

    [Fact]
    public void WhitespaceBetweenTokensIsDropped()
    {
        Assert.Equal(
            """
            {
              "a": 1
            }
            """.ReplaceLineEndings("\n"),
            GoJsonIndent.Format("{ \"a\" : 1 }", "  "));
    }
}
