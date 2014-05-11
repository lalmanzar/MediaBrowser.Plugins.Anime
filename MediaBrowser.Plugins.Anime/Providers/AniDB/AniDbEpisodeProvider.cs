﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Plugins.Anime.Configuration;

namespace MediaBrowser.Plugins.Anime.Providers.AniDB
{
    /// <summary>
    ///     The <see cref="AniDbEpisodeProvider" /> class provides episode metadata from AniDB.
    /// </summary>
    public class AniDbEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IHttpClient _httpClient;
        private readonly SeriesAnidbIndexSearch _anidbIndexSearch;

        /// <summary>
        ///     Creates a new instance of the <see cref="AniDbEpisodeProvider" /> class.
        /// </summary>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="httpClient">The HTTP client.</param>
        public AniDbEpisodeProvider(IServerConfigurationManager configurationManager, IHttpClient httpClient, ILogger logger, IApplicationPaths appPaths)
        {
            _configurationManager = configurationManager;
            _httpClient = httpClient;
            var downloader = new AniDbIdMapperDownloader(logger, appPaths);

            _anidbIndexSearch = new SeriesAnidbIndexSearch(configurationManager, httpClient,downloader);
        }

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>();

            cancellationToken.ThrowIfCancellationRequested();

            string seriesId = info.SeriesProviderIds.GetOrDefault(ProviderNames.AniDb);
            if (string.IsNullOrEmpty(seriesId))
                return result;

            string seriesFolder = await FindSeriesFolder(seriesId, info.ParentIndexNumber ?? 1, cancellationToken);
            if (string.IsNullOrEmpty(seriesFolder))
                return result;

            FileInfo xml = GetEpisodeXmlFile(info.IndexNumber, (info.ParentIndexNumber ?? 1) == 0, seriesFolder);
            if (xml == null || !xml.Exists)
                return result;

            result.Item = new Episode
            {
                IndexNumber = info.IndexNumber,
                ParentIndexNumber = info.ParentIndexNumber
            };

            result.HasMetadata = true;

            ParseEpisodeXml(xml, result.Item, info.MetadataLanguage);

            if (info.IndexNumber != null && info.IndexNumberEnd != null && info.IndexNumberEnd.Value > info.IndexNumber.Value) 
            {
                for (int i = info.IndexNumber.Value + 1; i <= info.IndexNumberEnd.Value; i++) {
                    FileInfo additionalXml = GetEpisodeXmlFile(i, (info.ParentIndexNumber ?? 1) == 0, seriesFolder);
                    if (additionalXml == null || !additionalXml.Exists)
                        continue;

                    ParseAdditionalEpisodeXml(additionalXml, result.Item, info.MetadataLanguage);
                }
            }

            return result;
        }

        private void ParseAdditionalEpisodeXml(FileInfo xml, Episode episode, string metadataLanguage)
        {
            var settings = new XmlReaderSettings {
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (StreamReader streamReader = xml.OpenText())
            using (XmlReader reader = XmlReader.Create(streamReader, settings)) {
                reader.MoveToContent();

                var titles = new List<Title>();

                while (reader.Read()) {
                    if (reader.NodeType == XmlNodeType.Element) {
                        switch (reader.Name) {
                            case "length":
                                string length = reader.ReadElementContentAsString();
                                if (!string.IsNullOrEmpty(length)) {
                                    long duration;
                                    if (long.TryParse(length, out duration))
                                        episode.RunTimeTicks += TimeSpan.FromMinutes(duration).Ticks;
                                }

                                break;
                            case "title":
                                string language = reader.GetAttribute("xml:lang");
                                string name = reader.ReadElementContentAsString();

                                titles.Add(new Title {
                                    Language = language,
                                    Type = "main",
                                    Name = name
                                });

                                break;
                        }
                    }
                }

                string title = titles.Localize(PluginConfiguration.Instance().TitlePreference, metadataLanguage).Name;
                if (!string.IsNullOrEmpty(title))
                    episode.Name += ", " + title;
            }
        }

        public string Name
        {
            get { return "AniDB"; }
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(Controller.Providers.EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        private async Task<string> FindSeriesFolder(string seriesId, int season, CancellationToken cancellationToken)
        {
            if (season != 1)
            {
                string id = await _anidbIndexSearch.FindSeriesByRelativeIndex(seriesId, season - 1, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(id))
                    return null;

                return AniDbSeriesProvider.CalculateSeriesDataPath(_configurationManager.ApplicationPaths, id);
            }

            string seriesDataPath = await AniDbSeriesProvider.GetSeriesData(_configurationManager.ApplicationPaths, _httpClient, seriesId, cancellationToken).ConfigureAwait(false);
            return Path.GetDirectoryName(seriesDataPath);
        }

        private void ParseEpisodeXml(FileInfo xml, Episode episode, string preferredMetadataLanguage)
        {
            var settings = new XmlReaderSettings
            {
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (StreamReader streamReader = xml.OpenText())
            using (XmlReader reader = XmlReader.Create(streamReader, settings))
            {
                reader.MoveToContent();

                var titles = new List<Title>();

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "length":
                                string length = reader.ReadElementContentAsString();
                                if (!string.IsNullOrEmpty(length))
                                {
                                    long duration;
                                    if (long.TryParse(length, out duration))
                                        episode.RunTimeTicks = TimeSpan.FromMinutes(duration).Ticks;
                                }

                                break;
                            case "airdate":
                                string airdate = reader.ReadElementContentAsString();
                                if (!string.IsNullOrEmpty(airdate))
                                {
                                    DateTime date;
                                    if (DateTime.TryParse(airdate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out date))
                                        episode.PremiereDate = date;
                                }

                                break;
                            case "rating":
                                int count;
                                float rating;
                                if (int.TryParse(reader.GetAttribute("count"), NumberStyles.Any, CultureInfo.InvariantCulture, out count) &&
                                    float.TryParse(reader.ReadElementContentAsString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rating))
                                {
                                    episode.VoteCount = count;
                                    episode.CommunityRating = rating;
                                }

                                break;
                            case "title":
                                string language = reader.GetAttribute("xml:lang");
                                string name = reader.ReadElementContentAsString();

                                titles.Add(new Title
                                {
                                    Language = language,
                                    Type = "main",
                                    Name = name
                                });

                                break;
                        }
                    }
                }

                string title = titles.Localize(PluginConfiguration.Instance().TitlePreference, preferredMetadataLanguage).Name;
                if (!string.IsNullOrEmpty(title))
                    episode.Name = title;
            }
        }

        private FileInfo GetEpisodeXmlFile(int? episodeNumber, bool special, string seriesDataPath)
        {
            if (episodeNumber == null)
            {
                return null;
            }

            string nameFormat = special ? "episode-S{0}.xml" : "episode-{0}.xml";
            string filename = Path.Combine(seriesDataPath, string.Format(nameFormat, episodeNumber.Value));
            return new FileInfo(filename);
        }
    }
}