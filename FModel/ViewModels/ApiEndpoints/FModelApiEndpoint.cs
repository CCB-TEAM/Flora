using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using RestSharp;
using Serilog;

namespace FModel.ViewModels.ApiEndpoints;

public class FModelApiEndpoint : AbstractApiProvider
{
    private News _news;
    private Donator[] _donators;
    private Game _game;
    private readonly IDictionary<string, CommunityDesign> _communityDesigns = new Dictionary<string, CommunityDesign>();

    public FModelApiEndpoint(RestClient client) : base(client) { }

    public async Task<News> GetNewsAsync(CancellationToken token, string game)
    {
        var request = new FRestRequest($"https://api.fmodel.app/v1/news/{Constants.APP_VERSION}");
        request.AddParameter("game", game);
        var response = await _client.ExecuteAsync<News>(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);
        return response.Data;
    }

    public News GetNews(CancellationToken token, string game)
    {
        return _news ??= GetNewsAsync(token, game).GetAwaiter().GetResult();
    }

    public async Task<Donator[]> GetDonatorsAsync()
    {
        var request = new FRestRequest($"https://api.fmodel.app/v1/donations/donators");
        var response = await _client.ExecuteAsync<Donator[]>(request).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);
        return response.Data;
    }

    public Donator[] GetDonators()
    {
        return _donators ??= GetDonatorsAsync().GetAwaiter().GetResult();
    }

    public async Task<Game> GetGamesAsync(CancellationToken token, string gameName)
    {
        var request = new FRestRequest($"https://api.fmodel.app/v1/games/{gameName}");
        var response = await _client.ExecuteAsync<Game>(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);
        return response.Data;
    }

    public Game GetGames(CancellationToken token, string gameName)
    {
        return _game ??= GetGamesAsync(token, gameName).GetAwaiter().GetResult();
    }

    public async Task<CommunityDesign> GetDesignAsync(string designName)
    {
        var request = new FRestRequest($"https://api.fmodel.app/v1/designs/{designName}");
        var response = await _client.ExecuteAsync<Community>(request).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);
        return response.Data != null ? new CommunityDesign(response.Data) : null;
    }

    public CommunityDesign GetDesign(string designName)
    {
        if (_communityDesigns.TryGetValue(designName, out var communityDesign) && communityDesign != null)
            return communityDesign;

        communityDesign = GetDesignAsync(designName).GetAwaiter().GetResult();
        _communityDesigns[designName] = communityDesign;
        return communityDesign;
    }

    public void CheckForUpdates(bool launch = false)
    {
        Log.Information("Update checks are disabled in this custom build.");
    }
}
