﻿using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

using Generellem.Rag;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Polly;
using Polly.Retry;

namespace Generellem.Services.Azure;

public class AzureSearchService(IConfiguration config, ILogger<AzureSearchService> logger) : IAzureSearchService
{
    const int VectorSearchDimensions = 1536;
    const string VectorAlgorithmConfigName = "hnsw-config";
    const string VectorProfileName = "generellem-vector-profile";

    readonly ILogger<AzureSearchService> logger = logger;

    readonly string? searchServiceAdminApiKey = config[GKeys.AzSearchServiceAdminApiKey];
    readonly string? searchServiceEndpoint = config[GKeys.AzSearchServiceEndpoint];
    readonly string? searchServiceIndex = config[GKeys.AzSearchServiceIndex];

    readonly ResiliencePipeline pipeline = 
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions())
            .AddTimeout(TimeSpan.FromSeconds(3))
            .Build();

    public virtual async Task CreateIndexAsync(CancellationToken cancelToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceAdminApiKey, nameof(searchServiceAdminApiKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceEndpoint, nameof(searchServiceEndpoint));

        Uri endpoint = new(searchServiceEndpoint);
        AzureKeyCredential credential = new(searchServiceAdminApiKey);

        SearchIndex searchIndex = new(searchServiceIndex)
        {
            Fields =
            {
                new SimpleField(nameof(TextChunk.ID), SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true, IsFacetable = true },
                new SearchableField(nameof(TextChunk.FileRef)) { IsFilterable = true, IsSortable = true, IsFacetable = true },
                new SearchableField(nameof(TextChunk.Content)) { IsFilterable = true },
                new VectorSearchField(nameof(TextChunk.Embedding), VectorSearchDimensions, VectorProfileName)
            },
            VectorSearch = new()
            {
                Profiles =
                {
                    new VectorSearchProfile(VectorProfileName, VectorAlgorithmConfigName)
                },
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(VectorAlgorithmConfigName)
                }
            },
        };
        SearchIndexClient indexClient = new(endpoint, credential);

        try
        {
            await pipeline.ExecuteAsync(
                async token => await indexClient.CreateOrUpdateIndexAsync(searchIndex, cancellationToken: token),
                cancelToken);
        }
        catch (RequestFailedException rfEx)
        {
            logger.LogError(GenerellemLogEvents.AuthorizationFailure, rfEx, "Please check credentials and exception details for more info.");
            throw;
        }    
    }

    public async Task DeleteFileRefsAsync(List<string> idsToDelete, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceAdminApiKey, nameof(searchServiceAdminApiKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceEndpoint, nameof(searchServiceEndpoint));

        Uri endpoint = new(searchServiceEndpoint);
        AzureKeyCredential credential = new(searchServiceAdminApiKey);

        SearchClient searchClient = new(endpoint, searchServiceIndex, credential);

        var batch = IndexDocumentsBatch.Delete(nameof(TextChunk.ID), idsToDelete);
        await searchClient.IndexDocumentsAsync(batch, null, cancellationToken);
    }

    public async Task<bool> DoesIndexExistAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceAdminApiKey, nameof(searchServiceAdminApiKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceEndpoint, nameof(searchServiceEndpoint));

        Uri endpoint = new(searchServiceEndpoint);
        AzureKeyCredential credential = new(searchServiceAdminApiKey);

        SearchIndexClient indexClient = new SearchIndexClient(endpoint, credential);

        try
        {
            SearchIndex index =
                await pipeline.ExecuteAsync(
                    async token => await indexClient.GetIndexAsync(searchServiceIndex, cancellationToken),
                    cancellationToken);
            return index != null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // 404 indicates the index does not exist
            return false;
        }
    }

    public async Task<List<TextChunk>> GetFileRefsAsync(string docSourcePrefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceAdminApiKey, nameof(searchServiceAdminApiKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceEndpoint, nameof(searchServiceEndpoint));

        Uri endpoint = new(searchServiceEndpoint);
        AzureKeyCredential credential = new(searchServiceAdminApiKey);

        SearchClient searchClient = new(endpoint, searchServiceIndex, credential);

        SearchOptions options = new()
        {
            Filter = $"search.ismatch('{docSourcePrefix}*', '{nameof(TextChunk.FileRef)}')"
        };
        options.Select.Add(nameof(TextChunk.ID));
        options.Select.Add(nameof(TextChunk.FileRef));

        List<TextChunk> chunks = new();

        try
        {
            Response<SearchResults<TextChunk>> response =
                await pipeline.ExecuteAsync(
                    async token => await searchClient.SearchAsync<TextChunk>(string.Empty, options, cancellationToken),
                    cancellationToken);

            await foreach (SearchResult<TextChunk> result in response.Value.GetResultsAsync())
                chunks.Add(result.Document);
        }
        catch (RequestFailedException rfEx)
        {
            logger.LogError(GenerellemLogEvents.AuthorizationFailure, rfEx, "Please check credentials and exception details for more info.");
            throw;
        }

        return chunks;
    }

    public virtual async Task UploadDocumentsAsync(List<TextChunk> documents, CancellationToken cancelToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceAdminApiKey, nameof(searchServiceAdminApiKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceEndpoint, nameof(searchServiceEndpoint));

        Uri endpoint = new(searchServiceEndpoint);
        AzureKeyCredential credential = new(searchServiceAdminApiKey);

        SearchClient searchClient = new(endpoint, searchServiceIndex, credential);

        try
        {
            await pipeline.ExecuteAsync(
                async token => await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.MergeOrUpload(documents), cancellationToken: token),
                cancelToken);
        }
        catch (RequestFailedException rfEx)
        {
            logger.LogError(GenerellemLogEvents.AuthorizationFailure, rfEx, "Please check credentials and exception details for more info.");
            throw;
        }    
    }

    public virtual async Task<List<TResponse>> SearchAsync<TResponse>(ReadOnlyMemory<float> embedding, CancellationToken cancelToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceAdminApiKey, nameof(searchServiceAdminApiKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(searchServiceEndpoint, nameof(searchServiceEndpoint));

        Uri endpoint = new(searchServiceEndpoint);
        AzureKeyCredential credential = new(searchServiceAdminApiKey);

        SearchClient searchClient = new(endpoint, searchServiceIndex, credential);

        var searchOptions = new SearchOptions
        {
            VectorSearch = new()
            {
                Queries = { new VectorizedQuery(embedding) { KNearestNeighborsCount = 3, Fields = { nameof(TextChunk.Embedding) } } }
            }
        };

        try
        {
            SearchResults<TResponse> results = await pipeline.ExecuteAsync<SearchResults<TResponse>>(
                async token => await searchClient.SearchAsync<TResponse>(searchOptions, cancellationToken: token),
                cancelToken);
        
            List<TResponse> chunks =
                (from chunk in results.GetResultsAsync().ToBlockingEnumerable(cancelToken)
                 select chunk.Document)
                .ToList();

            return chunks;
        }
        catch (RequestFailedException rfEx)
        {
            logger.LogError(GenerellemLogEvents.AuthorizationFailure, rfEx, "Please check credentials and exception details for more info.");
            throw;
        }
    }
}
