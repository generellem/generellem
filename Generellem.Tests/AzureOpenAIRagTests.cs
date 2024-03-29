﻿using Azure;
using Azure.AI.OpenAI;

using Generellem.Document.DocumentTypes;
using Generellem.Llm;
using Generellem.Rag.AzureOpenAI;
using Generellem.Repository;
using Generellem.Services;
using Generellem.Services.Azure;
using Generellem.Tests;

using Microsoft.Extensions.Logging;

namespace Generellem.Rag.Tests;

public class AzureOpenAIRagTests
{
    readonly Mock<IAzureSearchService> azSearchSvcMock = new();
    readonly Mock<IDynamicConfiguration> configMock = new();
    readonly Mock<IDocumentHashRepository> docHashRepMock = new();
    readonly Mock<IDocumentType> docTypeMock = new();
    readonly Mock<ILogger<AzureOpenAIRag>> logMock = new();
    readonly Mock<LlmClientFactory> llmClientFactMock = new();
    readonly Mock<OpenAIClient> openAIClientMock = new();
    readonly Mock<Response<Embeddings>> embeddingsMock = new();

    readonly AzureOpenAIRag azureOpenAIRag;
    readonly ReadOnlyMemory<float> embedding;

    public AzureOpenAIRagTests()
    {
        docTypeMock
            .Setup(doc => doc.GetTextAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync("text content");

        configMock
            .Setup(config => config[GKeys.AzOpenAIEndpointName])
            .Returns("https://generellem");
        configMock
            .Setup(config => config[GKeys.AzOpenAIEmbeddingName])
            .Returns("generellem-embedding");
        configMock
            .Setup(config => config[GKeys.AzOpenAIApiKey])
            .Returns("generellem-key");

        embedding = new ReadOnlyMemory<float>(TestEmbeddings.CreateEmbeddingArray());
        List<EmbeddingItem> embeddingItems =
        [
            AzureOpenAIModelFactory.EmbeddingItem(embedding)
        ];
        Embeddings embeddings = AzureOpenAIModelFactory.Embeddings(embeddingItems);

        openAIClientMock.Setup(client => client.GetEmbeddingsAsync(It.IsAny<EmbeddingsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embeddingsMock.Object);

        embeddingsMock.SetupGet(embed => embed.Value).Returns(embeddings);

        llmClientFactMock.Setup(llm => llm.CreateOpenAIClient()).Returns(openAIClientMock.Object);

        List<TextChunk> chunks =
        [
            new()
            {
                ID = "id1",
                Content = "chunk1",
                DocumentReference = "documentReference1"
            },
            new() 
            {
                ID = "id2",
                Content = "chunk2",
                DocumentReference = "documentReference2"
            }
        ];
        azSearchSvcMock
            .Setup(srchSvc => srchSvc.DoesIndexExistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        azSearchSvcMock
            .Setup(srchSvc => srchSvc.GetDocumentReferencesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        azSearchSvcMock
            .Setup(srchSvc => srchSvc.SearchAsync<TextChunk>(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);

        azureOpenAIRag = new AzureOpenAIRag(
            azSearchSvcMock.Object, configMock.Object, docHashRepMock.Object, llmClientFactMock.Object, logMock.Object);
    }

    [Fact]
    public async Task EmbedAsync_CallsGetEmbeddingsAsync()
    {
        TextProcessor.ChunkSize = 5000;
        TextProcessor.Overlap = 100;

        await azureOpenAIRag.EmbedAsync("Test document text", docTypeMock.Object, "file", CancellationToken.None);

        openAIClientMock.Verify(
            client => client.GetEmbeddingsAsync(It.IsAny<EmbeddingsOptions>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task EmbedAsync_WithNoOverlap_BreaksTextInto2Chunks()
    {
        int oldChunSize = TextProcessor.ChunkSize;
        int oldOverlap = TextProcessor.Overlap;
        TextProcessor.ChunkSize = 9;
        TextProcessor.Overlap = 0;

        await azureOpenAIRag.EmbedAsync("Test document text", docTypeMock.Object, "file", CancellationToken.None);

        openAIClientMock.Verify(
            client => client.GetEmbeddingsAsync(It.IsAny<EmbeddingsOptions>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        TextProcessor.ChunkSize = oldChunSize;
        TextProcessor.Overlap = oldOverlap;
    }

    [Fact]
    public async Task EmbedAsync_WithOverlap_BreaksTextInto3Chunks()
    {
        int oldChunSize = TextProcessor.ChunkSize;
        int oldOverlap = TextProcessor.Overlap;
        TextProcessor.ChunkSize = 9;
        TextProcessor.Overlap = 2;

        await azureOpenAIRag.EmbedAsync("Test document text", docTypeMock.Object, "file", CancellationToken.None);

        openAIClientMock.Verify(
            client => client.GetEmbeddingsAsync(It.IsAny<EmbeddingsOptions>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        TextProcessor.ChunkSize = oldChunSize;
        TextProcessor.Overlap = oldOverlap;
    }

    [Fact]
    public async Task EmbedAsync_OverlapsChunks()
    {
        int oldChunSize = TextProcessor.ChunkSize;
        int oldOverlap = TextProcessor.Overlap;
        TextProcessor.ChunkSize = 11;
        TextProcessor.Overlap = 3;

        await azureOpenAIRag.EmbedAsync("Test document text", docTypeMock.Object, "file", CancellationToken.None);

        openAIClientMock.Verify(
            client => client.GetEmbeddingsAsync(It.Is<EmbeddingsOptions>(eo => eo.Input[0] == "Test docume"), It.IsAny<CancellationToken>()),
            Times.Once);
        openAIClientMock.Verify(
            client => client.GetEmbeddingsAsync(It.Is<EmbeddingsOptions>(eo => eo.Input[0] == "ument text"), It.IsAny<CancellationToken>()),
            Times.Once);
        TextProcessor.ChunkSize = oldChunSize;
        TextProcessor.Overlap = oldOverlap;
    }

    [Fact]
    public async Task EmbedAsync_ReturnsEmbeddedTextChunks()
    {
        TextChunk expectedChunk = new()
        {
            Content = "Test document text",
            Embedding = TestEmbeddings.CreateEmbeddingArray(),
            DocumentReference = "file"
        };
        docTypeMock
            .Setup(doc => doc.GetTextAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync("Test document text");

        List<TextChunk> textChunks = await azureOpenAIRag.EmbedAsync("Test document text", docTypeMock.Object, "file", CancellationToken.None);

        TextChunk actualChunk = textChunks.First();
        Assert.Equal(expectedChunk.Content, actualChunk.Content);
        Assert.Equal(expectedChunk.DocumentReference, actualChunk.DocumentReference);
        float[] expectedEmbedding = expectedChunk.Embedding.ToArray();
        float[] actualEmbedding = actualChunk.Embedding.ToArray();
        Assert.Equal(expectedEmbedding.Length, actualEmbedding.Length);
        for (int i = 0; i < expectedEmbedding.Length; i++)
            Assert.Equal(expectedEmbedding[i], actualEmbedding[i]);
    }

    [Fact]
    public async Task EmbedAsync_WithRequestFailedException_LogsAnError()
    {
        openAIClientMock
            .Setup(client => client.GetEmbeddingsAsync(It.IsAny<EmbeddingsOptions>(), It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException("Unauthorized"));

        await Assert.ThrowsAsync<RequestFailedException>(async () => 
            await azureOpenAIRag.EmbedAsync("Test document text", docTypeMock.Object, "file", CancellationToken.None));

        logMock
            .Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.Once);
    }

    [Fact]
    public async Task IndexAsync_CallsCreateIndex()
    {
        List<TextChunk> chunks =
        [
            new()
            {
                Content = "Test document text",
                Embedding = TestEmbeddings.CreateEmbeddingArray(),
                DocumentReference = "file"
            }
        ];

        await azureOpenAIRag.IndexAsync(chunks, CancellationToken.None);

        azSearchSvcMock.Verify(srchSvc => srchSvc.CreateIndexAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task IndexAsync_WithEmptyChunks_DoesNotCallCreateIndex()
    {
        List<TextChunk> chunks = [];

        await azureOpenAIRag.IndexAsync(chunks, CancellationToken.None);

        azSearchSvcMock.Verify(srchSvc => srchSvc.CreateIndexAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task IndexAsync_CallsUploadDocuments()
    {
        List<TextChunk> chunks =
        [
            new()
            {
                Content = "Test document text",
                Embedding = TestEmbeddings.CreateEmbeddingArray(),
                DocumentReference = "file"
            }
        ];

        await azureOpenAIRag.IndexAsync(chunks, CancellationToken.None);

        azSearchSvcMock.Verify(srchSvc => 
            srchSvc.UploadDocumentsAsync(chunks, It.IsAny<CancellationToken>()), 
            Times.Once());
    }

    [Fact]
    public async Task IndexAsync_CallsUploadDocumentsWithCorrectChunks()
    {
        List<TextChunk> chunks =
        [
            new()
            {
                Content = "Test document text",
                Embedding = TestEmbeddings.CreateEmbeddingArray(),
                DocumentReference = "file"
            }
        ];

        await azureOpenAIRag.IndexAsync(chunks, CancellationToken.None);

        azSearchSvcMock.Verify(searchSvc => 
            searchSvc.UploadDocumentsAsync(It.Is<List<TextChunk>>(c => c == chunks), It.IsAny<CancellationToken>()), 
            Times.Once());
    }

    [Fact]
    public async Task IndexAsync_WithEmptyChunks_DoesNotCallUploadDocuments()
    {
        List<TextChunk> chunks = [];

        await azureOpenAIRag.IndexAsync(chunks, CancellationToken.None);

        azSearchSvcMock.Verify(srchSvc =>
            srchSvc.UploadDocumentsAsync(chunks, It.IsAny<CancellationToken>()),
            Times.Never());
    }
    
    [Fact]
    public async Task RemoveDeletedFilesAsync_WithNoDeletedFiles_DoesNotDeleteAnything()
    {
        string docSource = "Localhost:FileSystem";
        List<string> docSourceDocumentReferences = new List<string> { "documentReference1", "documentReference2" };

        await azureOpenAIRag.RemoveDeletedFilesAsync(docSource, docSourceDocumentReferences, CancellationToken.None);

        azSearchSvcMock.Verify(
            srch => srch.DeleteDocumentReferencesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveDeletedFilesAsync_WithDeletedFiles_DeletesCorrectFiles()
    {
        string docSource = "Localhost:FileSystem";
        List<string> docSourceDocumentReferences = new List<string> { "file1" };

        await azureOpenAIRag.RemoveDeletedFilesAsync(docSource, docSourceDocumentReferences, CancellationToken.None);

        azSearchSvcMock.Verify(
            srch => srch.DeleteDocumentReferencesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_CallsGetEmbeddingsAsync()
    {
        await azureOpenAIRag.SearchAsync("text", CancellationToken.None);

        openAIClientMock.Verify(
            client => client.GetEmbeddingsAsync(It.IsAny<EmbeddingsOptions>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task SearchAsync_CallsSearchAsyncWithEmbedding()
    {
        await azureOpenAIRag.SearchAsync("text", CancellationToken.None);

        azSearchSvcMock.Verify(srchSvc => 
            srchSvc.SearchAsync<TextChunk>(embedding, It.IsAny<CancellationToken>()), 
            Times.Once());
    }

    [Fact]
    public async Task SearchAsync_ReturnsChunkContents()
    {
        const string ExpectedContent = "chunk1";

        var result = await azureOpenAIRag.SearchAsync("text", CancellationToken.None);

        Assert.Equal(ExpectedContent, result.First());
    }

    [Fact]
    public async Task SearchAsync_WithRequestFailedExceptionOnGetEmbeddings_LogsAnError()
    {
        openAIClientMock
            .Setup(client => client.GetEmbeddingsAsync(It.IsAny<EmbeddingsOptions>(), It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException("Unauthorized"));

        await Assert.ThrowsAsync<RequestFailedException>(async () =>
            await azureOpenAIRag.SearchAsync("text", CancellationToken.None));

        logMock
            .Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.Once);
    }

    [Fact]
    public async Task SearchAsync_WithRequestFailedExceptionOnAzSearch_LogsAnError()
    {
        azSearchSvcMock
            .Setup(svc => svc.SearchAsync<TextChunk>(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException("Unauthorized"));

        await Assert.ThrowsAsync<RequestFailedException>(async () =>
            await azureOpenAIRag.SearchAsync("text", CancellationToken.None));

        logMock
            .Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.Once);
    }
}
