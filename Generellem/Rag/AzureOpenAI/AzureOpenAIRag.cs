﻿using Azure;
using Azure.AI.OpenAI;

using Generellem.Document.DocumentTypes;
using Generellem.Llm;
using Generellem.Services;
using Generellem.Services.Azure;

using Microsoft.Extensions.Configuration;

namespace Generellem.Rag.AzureOpenAI;

/// <summary>
/// Performs Retrieval-Augmented Generation (RAG) for Azure OpenAI
/// </summary>
public class AzureOpenAIRag : IRag
{
    readonly IAzureSearchService azSearchSvc;
    readonly IConfiguration config;

    readonly OpenAIClient openAIClient;

    public AzureOpenAIRag(IAzureSearchService azSearchSvc, IConfiguration config, LlmClientFactory llmClientFact)
    {
        this.azSearchSvc = azSearchSvc;
        this.config = config;
        this.openAIClient = llmClientFact.CreateOpenAIClient();
    }

    /// <summary>
    /// Breaks text into chunks and adds an embedding to each chunk based on the text in that chunk
    /// </summary>
    /// <param name="documentStream"><see cref="Stream"/> to read data from a document</param>
    /// <param name="docType"><see cref="IDocumentType"/> for extracting text from document</param>
    /// <param name="fileRef">Reference to file. e.g. either a path, url, or some other indicator of where the file came from</param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <returns>List of <see cref="TextChunk"/></returns>
    public virtual async Task<List<TextChunk>> EmbedAsync(Stream documentStream, IDocumentType docType, string fileRef, CancellationToken cancellationToken)
    {
        string fullText = await docType.GetTextAsync(documentStream, fileRef);
        List<TextChunk> chunks = TextProcessor.BreakIntoChunks(fullText, fileRef);
        EmbeddingsOptions embeddingsOptions = GetEmbeddingOptions(fullText);

        foreach (TextChunk chunk in chunks)
        {
            Response<Embeddings> embeddings = await openAIClient.GetEmbeddingsAsync(embeddingsOptions);

            chunk.Embedding = embeddings.Value.Data.First().Embedding;
        }

        return chunks;
    }

    /// <summary>
    /// Creates an Azure Search index (if it doesn't already exist), uploads document chunks, and indexes the chunks.
    /// </summary>
    /// <param name="chunks">Mulitple <see cref="TextChunk"/> instances for a document.</param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    public virtual async Task IndexAsync(List<TextChunk> chunks, CancellationToken cancellationToken)
    {
        await azSearchSvc.CreateIndexAsync();
        await azSearchSvc.UploadDocumentsAsync(chunks);
    }

    /// <summary>
    /// Performs Vector Search for chunks matching given text.
    /// </summary>
    /// <param name="text">Text for searching for matches</param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <returns>List of text chunks matching query</returns>
    public virtual async Task<List<string>> SearchAsync(string text, CancellationToken cancellationToken)
    {
        EmbeddingsOptions embeddingsOptions = GetEmbeddingOptions(text);

        Response<Embeddings> embeddings = await openAIClient.GetEmbeddingsAsync(embeddingsOptions);
        ReadOnlyMemory<float> embedding = embeddings.Value.Data.First().Embedding;
        List<TextChunk> chunks = await azSearchSvc.SearchAsync<TextChunk>(embedding);

        return
            (from chunk in chunks
             select chunk.Content)
            .ToList();
    }

    EmbeddingsOptions GetEmbeddingOptions(string text)
    {
        string? embeddingName = config[GKeys.AzOpenAIEmbeddingName];
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingName, nameof(embeddingName));

        EmbeddingsOptions embeddingsOptions = new(embeddingName, new string[] { text });

        return embeddingsOptions;
    }
}
