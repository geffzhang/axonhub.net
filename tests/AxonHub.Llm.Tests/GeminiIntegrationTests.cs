using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Gemini;
using GeminiModalityTokenCount = AxonHub.Llm.Transformer.Gemini.ModalityTokenCount;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Gemini end-to-end transformer pair
/// (llm/transformer/gemini/integraion_test.go): inbound and outbound wired
/// together, the testdata request fixtures replayed through both directions,
/// image responses, modality conversion and the grounding metadata round trip.
/// </summary>
public sealed class GeminiIntegrationTests
{
    private static readonly string TestDataRoot = Path.Combine(AppContext.BaseDirectory, "testdata", "gemini");

    private static GeminiInboundTransformer NewInbound() => new();

    private static GeminiOutboundTransformer NewOutbound()
        => GeminiOutboundTransformer.Create("https://generativelanguage.googleapis.com", "test-api-key");

    private static HttpRequestData Request(
        string path,
        Dictionary<string, List<string>>? query,
        string body)
        => new()
        {
            Path = path,
            Query = query,
            Headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };

    // ------------------------------------------------------------ GeminiTransformers_Integration

    [Theory]
    [InlineData(
        "simple text message",
        "/v1beta/models/gemini-2.5-flash:generateContent",
        false,
        "gemini-2.5-flash",
        1024L,
        "/v1beta/models/gemini-2.5-flash:generateContent",
        false,
        false)]
    [InlineData(
        "message with system instruction",
        "/v1beta/models/gemini-2.5-flash:generateContent",
        false,
        "gemini-2.5-flash",
        2048L,
        "/v1beta/models/gemini-2.5-flash:generateContent",
        false,
        false)]
    [InlineData(
        "message with modalities",
        "/v1beta/models/gemini-2.5-flash-image-preview:generateContent",
        false,
        "gemini-2.5-flash-image-preview",
        1024L,
        "/v1beta/models/gemini-2.5-flash-image-preview:generateContent",
        true,
        false)]
    [InlineData(
        "request with snake_case google_search tool",
        "/v1beta/models/gemini-2.5-flash:generateContent",
        false,
        "gemini-2.5-flash",
        256L,
        "/v1beta/models/gemini-2.5-flash:generateContent",
        false,
        true)]
    [InlineData(
        "streaming request with alt query",
        "/v1beta/models/gemini-2.5-flash:streamGenerateContent",
        true,
        "gemini-2.5-flash",
        512L,
        "/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
        false,
        false)]
    public void GeminiTransformers_Integration(
        string name,
        string requestPath,
        bool hasAltQuery,
        string expectedModel,
        long expectedMaxTokens,
        string expectedPath,
        bool expectModalities,
        bool expectToolTypes)
    {
        _ = name;

        var inbound = NewInbound();
        var outbound = NewOutbound();

        // Step 1: Transform Gemini request to LLM Request
        var httpReq = Request(requestPath, hasAltQuery ? new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["alt"] = ["sse"] } : null, BodyFor(name, requestPath, expectToolTypes));

        var chatReq = inbound.TransformRequest(httpReq);
        Assert.NotNull(chatReq);

        // Verify the transformation
        Assert.Equal(expectedModel, chatReq.Model);
        Assert.Equal(expectedMaxTokens, chatReq.MaxTokens!.Value);
        Assert.NotEmpty(chatReq.Messages);

        // Verify streaming behavior based on request path
        var expectedStream = requestPath.Contains("streamGenerateContent", StringComparison.Ordinal);
        Assert.NotNull(chatReq.Stream);
        Assert.Equal(expectedStream, chatReq.Stream!.Value);

        // Verify modalities if expected
        if (expectModalities)
        {
            Assert.Equal(["text", "image"], chatReq.Modalities);
        }

        // Step 2: Transform LLM Request to Gemini outbound request
        var outboundReq = outbound.TransformRequest(chatReq);
        Assert.NotNull(outboundReq);

        Assert.Equal("POST", outboundReq.Method);
        Assert.Equal("application/json", outboundReq.Headers!["Content-Type"][0]);

        // Verify the URL matches expected path
        Assert.Equal("https://generativelanguage.googleapis.com" + expectedPath, outboundReq.Url);

        // Verify the outbound request body can be unmarshaled
        var geminiReq = LlmJson.Deserialize<GenerateContentRequest>(outboundReq.Body)!;

        // Verify modalities are preserved
        if (expectModalities)
        {
            Assert.NotNull(geminiReq.GenerationConfig);
            Assert.Equal(["TEXT", "IMAGE"], geminiReq.GenerationConfig.ResponseModalities);
        }

        // Step 3: Simulate Gemini response and transform back
        var geminiResponse = new GenerateContentResponse
        {
            ResponseId = "test-response-123",
            ModelVersion = expectedModel,
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "This is a test response from Gemini." }],
                    },
                    FinishReason = "STOP",
                },
            ],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 15,
                CandidatesTokenCount = 25,
                TotalTokenCount = 40,
            },
        };

        var httpResp = new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(geminiResponse),
        };

        // Step 4: Transform Gemini response to LLM Response
        var chatResp = outbound.TransformResponse(httpResp);
        Assert.NotNull(chatResp);

        Assert.Equal("test-response-123", chatResp.Id);
        Assert.Equal("chat.completion", chatResp.Object);
        Assert.Equal(expectedModel, chatResp.Model);
        var choice = Assert.Single(chatResp.Choices);
        var message = choice.Message;
        Assert.NotNull(message);
        Assert.Equal("assistant", message.Role);
        Assert.Equal("This is a test response from Gemini.", message.Content!.Content);
        Assert.Equal("stop", choice.FinishReason);

        // Step 5: Transform LLM Response back to Gemini format
        var finalHttpResp = inbound.TransformResponse(chatResp);
        Assert.NotNull(finalHttpResp);

        Assert.Equal(200, finalHttpResp.StatusCode);
        Assert.Equal("application/json", finalHttpResp.Headers!["Content-Type"][0]);

        var finalGeminiResp = LlmJson.Deserialize<GenerateContentResponse>(finalHttpResp.Body)!;
        Assert.Equal("test-response-123", finalGeminiResp.ResponseId);
        Assert.Equal(expectedModel, finalGeminiResp.ModelVersion);
        var finalCandidates = finalGeminiResp.Candidates;
        Assert.NotNull(finalCandidates);
        var finalCandidate = Assert.Single(finalCandidates);
        Assert.NotNull(finalCandidate.Content);
        Assert.Equal("model", finalCandidate.Content.Role);
    }

    /// <summary>The reference's per-case request JSON, one fixture per
    /// request shape; the system-instruction case gets its own body because
    /// its maxOutputTokens/temperature differ from the plain-text shape.</summary>
    private static string BodyFor(string name, string requestPath, bool expectToolTypes)
    {
        if (expectToolTypes)
        {
            return """
                {
                    "contents": [
                        {
                            "role": "user",
                            "parts": [
                                {"text": "Search the web for the latest AI announcement."}
                            ]
                        }
                    ],
                    "tools": [
                        {
                            "google_search": {}
                        }
                    ],
                    "generationConfig": {
                        "maxOutputTokens": 256
                    }
                }
                """;
        }

        return requestPath switch
        {
            "/v1beta/models/gemini-2.5-flash:generateContent" when name == "message with system instruction" => """
                {
                    "systemInstruction": {
                        "parts": [
                            {"text": "You are a helpful assistant."}
                        ]
                    },
                    "contents": [
                        {
                            "role": "user",
                            "parts": [
                                {"text": "What is the capital of France?"}
                            ]
                        }
                    ],
                    "generationConfig": {
                        "maxOutputTokens": 2048,
                        "temperature": 0.7
                    }
                }
                """,
            "/v1beta/models/gemini-2.5-flash-image-preview:generateContent" => """
                {
                    "contents": [
                        {
                            "role": "user",
                            "parts": [
                                {"text": "Draw a green apple"}
                            ]
                        }
                    ],
                    "generationConfig": {
                        "maxOutputTokens": 1024,
                        "temperature": 1,
                        "topP": 1,
                        "responseModalities": ["TEXT", "IMAGE"]
                    }
                }
                """,
            "/v1beta/models/gemini-2.5-flash:streamGenerateContent" => """
                {
                    "contents": [
                        {
                            "role": "user",
                            "parts": [
                                {"text": "Hello streaming!"}
                            ]
                        }
                    ],
                    "generationConfig": {
                        "maxOutputTokens": 512
                    }
                }
                """,
            _ => """
                {
                    "contents": [
                        {
                            "role": "user",
                            "parts": [
                                {"text": "Hello, Gemini!"}
                            ]
                        }
                    ],
                    "generationConfig": {
                        "maxOutputTokens": 1024
                    }
                }
                """,
        };
    }

    // ------------------------------------------------------------ GroundingRoundTrip

    [Fact]
    public void GeminiTransformResponse_GroundingRoundTripIntegration()
    {
        var inbound = NewInbound();
        var outbound = NewOutbound();

        var geminiResponse = new GenerateContentResponse
        {
            ResponseId = "resp_gemini_grounding_roundtrip",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Grounded answer" }],
                    },
                    FinishReason = "STOP",
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["grounded query"],
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://example.com/gemini",
                                    Title = "Gemini Source",
                                },
                            },
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new Segment
                                {
                                    StartIndex = 0,
                                    EndIndex = 8,
                                    Text = "Grounded",
                                },
                                GroundingChunkIndices = [0],
                            },
                        ],
                    },
                },
            ],
        };

        var httpResp = new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(geminiResponse),
        };

        var chatResp = outbound.TransformResponse(httpResp);
        Assert.NotNull(chatResp);
        var choice = Assert.Single(chatResp.Choices);
        var message = choice.Message;
        Assert.NotNull(message);
        Assert.Equal("assistant", message.Role);
        Assert.Equal("Grounded answer", message.Content!.Content);
        var annotations = message.Annotations;
        Assert.NotNull(annotations);

        var annotation = Assert.Single(annotations);
        Assert.Equal("url_citation", annotation.Type);
        Assert.NotNull(annotation.UrlCitation);
        Assert.Equal("https://example.com/gemini", annotation.UrlCitation.Url);
        Assert.Equal("Gemini Source", annotation.UrlCitation.Title);
        Assert.NotNull(annotation.StartIndex);
        Assert.Equal(0, annotation.StartIndex.Value);
        Assert.NotNull(annotation.EndIndex);
        Assert.Equal(8, annotation.EndIndex.Value);

        var transformerMetadata = choice.TransformerMetadata;
        Assert.NotNull(transformerMetadata);
        var groundingMetadata = Xmap.GetPtr<GroundingMetadata>(
            transformerMetadata,
            GeminiOutboundConvert.TransformerMetadataKeyGroundingMetadata);
        Assert.NotNull(groundingMetadata);
        Assert.Equal(["grounded query"], groundingMetadata.WebSearchQueries);
        var groundingChunks = groundingMetadata.GroundingChunks;
        Assert.NotNull(groundingChunks);
        var groundingChunk = Assert.Single(groundingChunks);
        Assert.NotNull(groundingChunk.Web);
        Assert.Equal("https://example.com/gemini", groundingChunk.Web.URI);
        Assert.Equal("Gemini Source", groundingChunk.Web.Title);

        var finalHttpResp = inbound.TransformResponse(chatResp);
        Assert.NotNull(finalHttpResp);

        var finalGeminiResp = LlmJson.Deserialize<GenerateContentResponse>(finalHttpResp.Body)!;
        Assert.Equal("resp_gemini_grounding_roundtrip", finalGeminiResp.ResponseId);
        Assert.Equal("gemini-2.5-flash", finalGeminiResp.ModelVersion);
        var finalCandidates = finalGeminiResp.Candidates;
        Assert.NotNull(finalCandidates);
        var finalCandidate = Assert.Single(finalCandidates);
        Assert.NotNull(finalCandidate.Content);
        Assert.Equal("model", finalCandidate.Content.Role);
        Assert.NotNull(finalCandidate.GroundingMetadata);
        Assert.Equal(["grounded query"], finalCandidate.GroundingMetadata.WebSearchQueries);
        var finalChunks = finalCandidate.GroundingMetadata.GroundingChunks;
        Assert.NotNull(finalChunks);
        var finalChunk = Assert.Single(finalChunks);
        Assert.NotNull(finalChunk.Web);
        Assert.Equal("https://example.com/gemini", finalChunk.Web.URI);
        Assert.Equal("Gemini Source", finalChunk.Web.Title);
        Assert.Null(finalCandidate.CitationMetadata);
    }

    // ------------------------------------------------------------ TransformRequest_Integration

    [Theory]
    [InlineData("gemini-simple.request.json")]
    [InlineData("gemini-tools-jsonschema.request.json")]
    [InlineData("gemini-thinking.request.json")]
    public void TransformRequest_Integration(string requestFile)
    {
        var inbound = NewInbound();
        var outbound = NewOutbound();

        var wantReq = LlmJson.Deserialize<GenerateContentRequest>(
            File.ReadAllText(Path.Combine(TestDataRoot, requestFile)))!;

        // Use a mock path for the inbound transformer
        var httpReq = Request(
            "/v1beta/models/gemini-2.5-flash:generateContent",
            null,
            LlmJson.Serialize(wantReq));

        var chatReq = inbound.TransformRequest(httpReq);
        Assert.NotNull(chatReq);

        var outboundReq = outbound.TransformRequest(chatReq);

        var gotReq = LlmJson.Deserialize<GenerateContentRequest>(outboundReq.Body)!;

        // Semantic equality with json.RawMessage comparer semantics: the
        // Parameters/ParametersJsonSchema blobs compare as JSON values.
        ResponsesStreamTestSupport.AssertJsonEquivalent(
            LlmJson.Serialize(wantReq),
            LlmJson.Serialize(gotReq),
            requestFile);
    }

    // ------------------------------------------------------------ GeminiImageResponse_Integration

    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void GeminiImageResponse_ImageOnlyResponse()
    {
        var chatResp = TransformImageResponse(new GenerateContentResponse
        {
            ResponseId = "resp-image-only",
            ModelVersion = "gemini-2.5-flash-image-preview",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                InlineData = new Blob { MIMEType = "image/png", Data = PngBase64 },
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 6,
                CandidatesTokenCount = 1299,
                TotalTokenCount = 1305,
                PromptTokensDetails = [new GeminiModalityTokenCount { Modality = "TEXT", TokenCount = 6 }],
                CandidatesTokensDetails = [new GeminiModalityTokenCount { Modality = "IMAGE", TokenCount = 1290 }],
            },
        });

        Assert.Equal("resp-image-only", chatResp.Id);
        var imageChoice = Assert.Single(chatResp.Choices);
        var imageMessage = imageChoice.Message;
        Assert.NotNull(imageMessage);
        Assert.Equal("assistant", imageMessage.Role);

        // Verify image content
        var multipleContent = imageMessage.Content!.MultipleContent;
        Assert.NotNull(multipleContent);
        var imagePart = Assert.Single(multipleContent);
        Assert.Equal("image_url", imagePart.Type);
        Assert.NotNull(imagePart.ImageUrl);
        Assert.Equal("data:image/png;base64," + PngBase64, imagePart.ImageUrl.Url);

        // Verify modality token details
        var usage = chatResp.Usage;
        Assert.NotNull(usage);
        var promptDetails = usage.PromptModalityTokenDetails;
        Assert.NotNull(promptDetails);
        Assert.Equal("TEXT", Assert.Single(promptDetails).Modality);
        var completionDetails = usage.CompletionModalityTokenDetails;
        Assert.NotNull(completionDetails);
        Assert.Equal("IMAGE", Assert.Single(completionDetails).Modality);
    }

    [Fact]
    public void GeminiImageResponse_TextAndImageMixedResponse()
    {
        var chatResp = TransformImageResponse(new GenerateContentResponse
        {
            ResponseId = "resp-text-image",
            ModelVersion = "gemini-2.5-flash-image-preview",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part { Text = "Here is the green apple you requested:" },
                            new Part
                            {
                                InlineData = new Blob { MIMEType = "image/png", Data = PngBase64 },
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 10,
                CandidatesTokenCount = 1500,
                TotalTokenCount = 1510,
            },
        });

        Assert.Equal("resp-text-image", chatResp.Id);
        var mixedChoice = Assert.Single(chatResp.Choices);
        var mixedMessage = mixedChoice.Message;
        Assert.NotNull(mixedMessage);

        // Should have multipart content with text first, then image
        var mixedContent = mixedMessage.Content!.MultipleContent;
        Assert.NotNull(mixedContent);
        Assert.Equal(2, mixedContent.Count);

        // First part should be text
        Assert.Equal("text", mixedContent[0].Type);
        Assert.Equal("Here is the green apple you requested:", mixedContent[0].Text);

        // Second part should be image
        Assert.Equal("image_url", mixedContent[1].Type);
        Assert.Contains("data:image/png;base64,", mixedContent[1].ImageUrl!.Url);
    }

    [Fact]
    public void GeminiImageResponse_MultipleImagesResponse()
    {
        var chatResp = TransformImageResponse(new GenerateContentResponse
        {
            ResponseId = "resp-multi-image",
            ModelVersion = "gemini-2.5-flash-image-preview",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                InlineData = new Blob { MIMEType = "image/png", Data = "image1base64data" },
                            },
                            new Part
                            {
                                InlineData = new Blob { MIMEType = "image/jpeg", Data = "image2base64data" },
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        });

        Assert.Equal("resp-multi-image", chatResp.Id);
        var multiChoice = Assert.Single(chatResp.Choices);
        var multiMessage = multiChoice.Message;
        Assert.NotNull(multiMessage);

        // Should have 2 images
        var multiContent = multiMessage.Content!.MultipleContent;
        Assert.NotNull(multiContent);
        Assert.Equal(2, multiContent.Count);

        // First image (PNG)
        Assert.Equal("image_url", multiContent[0].Type);
        Assert.Equal("data:image/png;base64,image1base64data", multiContent[0].ImageUrl!.Url);

        // Second image (JPEG)
        Assert.Equal("image_url", multiContent[1].Type);
        Assert.Equal("data:image/jpeg;base64,image2base64data", multiContent[1].ImageUrl!.Url);
    }

    [Fact]
    public void GeminiImageResponse_DifferentMimeTypes()
    {
        var chatResp = TransformImageResponse(new GenerateContentResponse
        {
            ResponseId = "resp-mime-types",
            ModelVersion = "gemini-2.5-flash-image-preview",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                InlineData = new Blob { MIMEType = "image/webp", Data = "webpdata" },
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        });

        var mimeChoice = Assert.Single(chatResp.Choices);
        var mimeMessage = mimeChoice.Message;
        Assert.NotNull(mimeMessage);
        var mimeContent = mimeMessage.Content!.MultipleContent;
        Assert.NotNull(mimeContent);
        var mimePart = Assert.Single(mimeContent);
        Assert.Equal("data:image/webp;base64,webpdata", mimePart.ImageUrl!.Url);
    }

    [Fact]
    public void GeminiImageResponse_ImageResponseRoundTrip()
    {
        var inbound = NewInbound();
        var outbound = NewOutbound();

        var geminiResponse = new GenerateContentResponse
        {
            ResponseId = "resp-roundtrip",
            ModelVersion = "gemini-2.5-flash-image-preview",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part { Text = "Here is your image:" },
                            new Part
                            {
                                InlineData = new Blob { MIMEType = "image/png", Data = PngBase64 },
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 10,
                CandidatesTokenCount = 1300,
                TotalTokenCount = 1310,
            },
        };

        var httpResp = new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(geminiResponse),
        };

        // Step 1: Gemini -> LLM
        var chatResp = outbound.TransformResponse(httpResp);

        // Step 2: LLM -> Gemini
        var finalHttpResp = inbound.TransformResponse(chatResp);

        var finalGeminiResp = LlmJson.Deserialize<GenerateContentResponse>(finalHttpResp.Body)!;

        // Verify round-trip preserved the structure
        Assert.Equal("resp-roundtrip", finalGeminiResp.ResponseId);
        var roundtripCandidates = finalGeminiResp.Candidates;
        Assert.NotNull(roundtripCandidates);
        var roundtripCandidate = Assert.Single(roundtripCandidates);
        Assert.NotNull(roundtripCandidate.Content);
        var roundtripParts = roundtripCandidate.Content!.Parts;
        Assert.NotNull(roundtripParts);
        Assert.Equal("model", roundtripCandidate.Content.Role);
        Assert.Equal(2, roundtripParts.Count);

        // First part should be text
        Assert.Equal("Here is your image:", roundtripParts[0].Text);

        // Second part should be inline data (image)
        var inlineData = roundtripParts[1].InlineData;
        Assert.NotNull(inlineData);
        Assert.Equal("image/png", inlineData.MIMEType);
        Assert.Equal(PngBase64, inlineData.Data);
    }

    private static LlmResponse TransformImageResponse(GenerateContentResponse geminiResponse)
        => NewOutbound().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(geminiResponse),
        });

    // ------------------------------------------------------------ ModalitiesConversion

    [Theory]
    [InlineData("text", "TEXT")]
    [InlineData("text,image", "TEXT,IMAGE")]
    [InlineData("text,image,audio", "TEXT,IMAGE,AUDIO")]
    public void ModalitiesConversion(string llmModalitiesCsv, string geminiModalitiesCsv)
    {
        var llmModalities = llmModalitiesCsv.Split(',').ToList();
        var geminiModalities = geminiModalitiesCsv.Split(',').ToList();

        // Test LLM -> Gemini conversion
        Assert.Equal(geminiModalities, GeminiConvert.ConvertLlmModalitiesToGemini(llmModalities));

        // Test Gemini -> LLM conversion
        Assert.Equal(llmModalities, GeminiConvert.ConvertGeminiModalitiesToLlm(geminiModalities));
    }

    // ------------------------------------------------------------ ModalitiesRoundTrip

    [Fact]
    public void ModalitiesRoundTrip()
    {
        var inbound = NewInbound();
        var outbound = NewOutbound();

        var geminiReq = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = "user",
                    Parts = [new Part { Text = "Draw a green apple" }],
                },
            ],
            GenerationConfig = new GenerationConfig
            {
                MaxOutputTokens = 1024,
                Temperature = 1.0,
                TopP = 1.0,
                ResponseModalities = ["TEXT", "IMAGE"],
            },
        };

        var reqBody = GoJson.Marshal(geminiReq);

        // Transform Gemini -> LLM
        var httpReq = Request("/v1beta/models/gemini-2.5-flash-image-preview:generateContent", null, Encoding.UTF8.GetString(reqBody));

        var chatReq = inbound.TransformRequest(httpReq);
        Assert.Equal(["text", "image"], chatReq.Modalities);

        // Transform LLM -> Gemini
        var outboundReq = outbound.TransformRequest(chatReq);

        var gotGeminiReq = LlmJson.Deserialize<GenerateContentRequest>(outboundReq.Body)!;

        // Verify modalities are preserved
        Assert.NotNull(gotGeminiReq.GenerationConfig);
        Assert.Equal(["TEXT", "IMAGE"], gotGeminiReq.GenerationConfig.ResponseModalities);
    }

    // ------------------------------------------------------------ InboundTransformRequest_SnakeCaseGoogleSearchTool

    [Fact]
    public void InboundTransformRequest_SnakeCaseGoogleSearchTool()
    {
        var inbound = NewInbound();

        var httpReq = Request(
            "/v1beta/models/gemini-2.5-flash:generateContent",
            null,
            """
            {
                "contents": [
                    {
                        "role": "user",
                        "parts": [
                            {"text": "Search the web for the latest AI announcement."}
                        ]
                    }
                ],
                "tools": [
                    {
                        "google_search": {}
                    }
                ],
                "generationConfig": {
                    "maxOutputTokens": 256
                }
            }
            """);

        var chatReq = inbound.TransformRequest(httpReq);
        Assert.NotNull(chatReq);
        var tools = chatReq.Tools;
        Assert.NotNull(tools);
        var tool = Assert.Single(tools);
        Assert.Equal(ToolTypes.GoogleSearch, tool.Type);
        Assert.NotNull(tool.Google);
        Assert.NotNull(tool.Google.Search);
    }
}
