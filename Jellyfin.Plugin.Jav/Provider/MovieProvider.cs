﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jav.Extensions;
using Jellyfin.Plugin.Jav.Model;
using Jellyfin.Plugin.Jav.Service;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using static System.Globalization.NumberFormatInfo;
using static System.StringComparison;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;

namespace Jellyfin.Plugin.Jav.Provider
{
    public class MovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private readonly JavService _javService;
        private readonly ILogger<MovieProvider> _logger;

        public MovieProvider(JavService javService, ILogger<MovieProvider> logger)
        {
            _javService = javService;
            _logger = logger;
        }

        public string Name => Constants.PluginName;

        public int Order => 1;

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var indexes = await _javService.SearchMovie(searchInfo.Name).ConfigureAwait(false) ?? new List<MovieIndex>(0);

            return from index in indexes
                select new RemoteSearchResult
                {
                    Name = $"{index.Number} {index.Title}",
                    SearchProviderName = Name,
                    ProductionYear = index.ReleaseDate?.Year,
                    PremiereDate = index.ReleaseDate,
                    ImageUrl = index.ThumbUrl?.ToString(),
                    ProviderIds = index.DetailPageUrl != null && !string.IsNullOrEmpty(index.DetailPageUrl.AbsoluteUri)
                        ? new Dictionary<string, string> { { "DetailPageUrl", index.DetailPageUrl.AbsoluteUri.Contains("locale", StringComparison.Ordinal) ? index.DetailPageUrl.AbsoluteUri : index.DetailPageUrl.AbsoluteUri + "?locale=zh" }, { "Provider", index.Provider }, { "Title", index.Title }, { "Number", index.Number } }
                        : new Dictionary<string, string>()
                };
        }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(info.Path);
            name = name.Contains('.', Ordinal) ? name[..name.LastIndexOf('.')] : name;
            _logger.LogInformation("GetMetadata, name={Name}", name);
            _logger.LogInformation("GetMetadata: ProviderIds={@ProviderIds}", info.ProviderIds);
            var configuration = Plugin.Instance.Configuration;
            var movie = info.IsAutomated ? info.GetMovie() : null;
            _logger.LogInformation("GetMetadata From Remote, name={Name}", name);

            string detailPageUrl;
            if (info.ProviderIds.TryGetValue("DetailPageUrl", out string? url))
            {
                detailPageUrl = url ?? string.Empty;
            }
            else
            {
                detailPageUrl = string.Empty;
            }

            string provider;
            if (info.ProviderIds.TryGetValue("Provider", out string? provider1))
            {
                provider = provider1 ?? string.Empty;
            }
            else
            {
                provider = string.Empty;
            }

            string title;
            if (info.ProviderIds.TryGetValue("Title", out string? title1))
            {
                title = title1 ?? string.Empty;
            }
            else
            {
                title = string.Empty;
            }

            string number;
            if (info.ProviderIds.TryGetValue("Number", out string? number1))
            {
                number = number1 ?? string.Empty;
            }
            else
            {
                number = string.Empty;
            }

            if (!string.IsNullOrEmpty(detailPageUrl))
            {
                string encodedUrl = HttpUtility.UrlEncode(detailPageUrl);
                movie = await _javService.AutoSearchMovieWithUri(number, title, encodedUrl, provider).ConfigureAwait(false);
            }
            else
            {
                movie = await _javService.AutoSearchMovie(name).ConfigureAwait(false);
            }

            if (movie == null)
            {
                return new MetadataResult<Movie> { HasMetadata = false };
            }

            info.SetMovie(movie);

            var parameters = AsParameters(movie);

            string[] tagsVar = new[]
                {
                    movie.Series ?? string.Empty,
                    movie.Studio ?? string.Empty,
                    movie.Label ?? string.Empty
                }.Where(tag => !string.IsNullOrEmpty(tag)).ToArray();

            string[] studioVar = new[]
                {
                    movie.Studio ?? string.Empty,
                }.Where(tag => !string.IsNullOrEmpty(tag)).ToArray();

            var metaData = new Movie
            {
                Name = RenderTemplate(configuration.Template.TitleTemplate, parameters, configuration.Template.PlaceholderIfNull),
                Tagline = RenderTemplate(configuration.Template.TaglineTemplate, parameters, configuration.Template.PlaceholderIfNull),
                OriginalTitle = movie.Title,
                OfficialRating = movie.CommunityRating?.ToString(InvariantInfo),
                PremiereDate = movie.ReleaseDate,
                ProductionYear = movie.ReleaseDate?.Year,
                Genres = configuration.Replacement.EnableGenreReplacement
                    ? movie.Genres.Select(genre => configuration.Replacement.GenreReplacementTable.GetValueOrDefault(genre, genre)).ToArray()
                    : movie.Genres.ToArray(),
                CommunityRating = (float?)movie.CommunityRating,
                Studios = studioVar,
                Tags = tagsVar,
                SortName = movie.Number,
                ForcedSortName = movie.Number,
                ExternalId = movie.Number,
                HomePageUrl = movie.DetailPageUrl.ToString()
            };

            if (configuration.General.EnableCollectionsBySeries)
            {
                metaData.CollectionName = movie.Series;
            }

            if (configuration.General.EnableChineseSubtitleGenre && Constants.ChineseSubtitleSuffix.IsMatch(info.Name))
            {
                metaData.AddGenre("中文字幕");
            }

            var result = new MetadataResult<Movie> { HasMetadata = true, Item = metaData };
            if (movie.Director != null)
            {
                result.AddPerson(new PersonInfo { Name = movie.Director, Type = PersonKind.Director });
            }

            var actors = configuration.Replacement.EnableActorReplacement
                ? movie.Actors.Select(actor => configuration.Replacement.ActorReplacementTable.GetValueOrDefault(actor, actor)).ToArray()
                : movie.Actors.ToArray();
            var avatar = await _javService.SearchAvatarByName(actors).ConfigureAwait(false);
            foreach (var actor in actors)
            {
                result.AddPerson(new PersonInfo { Name = actor, Type = PersonKind.Actor, ImageUrl = avatar?.GetValueOrDefault(actor) });
            }

            return result;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) => _javService.GetImage(new Uri(url));

        private static Dictionary<string, string?> AsParameters(Model.Movie movie) =>
            new()
            {
                { @"{number}", movie.Number },
                { @"{title}", movie.Title },
                { @"{actors}", movie.Actors.Any() ? string.Join(", ", movie.Actors) : null },
                { @"{first_actor}", movie.Actors.Any() ? movie.Actors[0] : null },
                { @"{series}", movie.Series },
                { @"{studio}", movie.Studio },
                { @"{label}", movie.Label },
                { @"{director}", movie.Director },
                { @"{year}", movie.ReleaseDate?.ToString("yyyy", CultureInfo.InvariantCulture) },
                { @"{month}", movie.ReleaseDate?.ToString("MM", CultureInfo.InvariantCulture) },
                { @"{date}", movie.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            };

        private static string RenderTemplate(string template, Dictionary<string, string?> parameters, string placeholderIfNull)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return string.Empty;
            }

            return parameters.Where(kvp => template.Contains(kvp.Key, InvariantCultureIgnoreCase))
                .Aggregate(new StringBuilder(template), (stringBuilder, pair) => stringBuilder.Replace(pair.Key, pair.Value ?? placeholderIfNull))
                .ToString()
                .Trim();
        }
    }
}
