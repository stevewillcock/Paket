﻿using System;
using System.IO;
using System.Net;
using System.Reflection;

namespace Paket.Bootstrapper
{
    internal class GitHubDownloadStrategy : IDownloadStrategy
    {
        private PrepareWebClientDelegate PrepareWebClient { get; set; }
        private PrepareWebRequestDelegate PrepareWebRequest { get; set; }
        private GetDefaultWebProxyForDelegate GetDefaultWebProxyFor { get; set; }
        public string Name { get { return "Github"; } }
        public IDownloadStrategy FallbackStrategy { get; set; }

        public GitHubDownloadStrategy(PrepareWebClientDelegate prepareWebClient, PrepareWebRequestDelegate prepareWebRequest, GetDefaultWebProxyForDelegate getDefaultWebProxyFor)
        {
            PrepareWebClient = prepareWebClient;
            PrepareWebRequest = prepareWebRequest;
            GetDefaultWebProxyFor = getDefaultWebProxyFor;
        }

        public string GetLatestVersion(bool ignorePrerelease)
        {
            const string latest = "https://github.com/fsprojects/Paket/releases/latest";
            const string releases = "https://github.com/fsprojects/Paket/releases";
            using (var client = new WebClient())
            {
                if (ignorePrerelease)
                {
                    PrepareWebClient(client, latest);
                    var data = client.DownloadString(latest);
                    var title = data.Substring(data.IndexOf("<title>"), (data.IndexOf("</title>") + 8 - data.IndexOf("<title>"))); // grabs everything in the <title> tag
                    var version = title.Split(' ')[1]; // <title>Release, 1.34.0, etc, etc, etc <-- the release number is the second part fo this split string
                    return version;
                }
                else
                {
                    // have to iterate over the releases in the old manner.
                    var latestVersion = "";
                    var start = 0;
                    PrepareWebClient(client, releases);
                    var data = client.DownloadString(releases);
                    while (latestVersion == "")
                    {
                        start = data.IndexOf("Paket/tree/", start) + 11;
                        var end = data.IndexOf("\"", start);
                        latestVersion = data.Substring(start, end - start);
                        if (latestVersion.Contains("-") && ignorePrerelease)
                            latestVersion = "";
                    }
                    return latestVersion;
                }
            }
        }

        public void DownloadVersion(string latestVersion, string target, bool silent)
        {
            var url = String.Format("https://github.com/fsprojects/Paket/releases/download/{0}/paket.exe", latestVersion);
            if (!silent)
                Console.WriteLine("Starting download from {0}", url);

            var request = PrepareWebRequest(url);

            using (var httpResponse = (HttpWebResponse)request.GetResponse())
            {
                using (var httpResponseStream = httpResponse.GetResponseStream())
                {
                    const int bufferSize = 4096;
                    byte[] buffer = new byte[bufferSize];
                    var tmpFile = Path.GetTempFileName();

                    using (var fileStream = File.Create(tmpFile))
                    {
                        int bytesRead;
                        while ((bytesRead = httpResponseStream.Read(buffer, 0, bufferSize)) != 0)
                        {
                            fileStream.Write(buffer, 0, bytesRead);
                        }
                    }

                    File.Copy(tmpFile, target, true);
                    File.Delete(tmpFile);
                }
            }
        }

        public void SelfUpdate(string latestVersion, bool silent)
        {
            var executingAssembly = Assembly.GetExecutingAssembly();
            string exePath = executingAssembly.Location;
            var localVersion = BootstrapperHelper.GetLocalFileVersion(exePath);
            if (localVersion.StartsWith(latestVersion))
            {
                if (!silent)
                    Console.WriteLine("Bootstrapper is up to date. Nothing to do.");
                return;
            }

            var url = String.Format("https://github.com/fsprojects/Paket/releases/download/{0}/paket.bootstrapper.exe", latestVersion);
            if (!silent)
                Console.WriteLine("Starting download of bootstrapper from {0}", url);

            var request = PrepareWebRequest(url);

            string renamedPath = Path.GetTempFileName();
            string tmpDownloadPath = Path.GetTempFileName();

            using (var httpResponse = (HttpWebResponse)request.GetResponse())
            {
                using (Stream httpResponseStream = httpResponse.GetResponseStream(), toStream = File.Create(tmpDownloadPath))
                {
                    httpResponseStream.CopyTo(toStream);
                }
            }
            try
            {
                BootstrapperHelper.FileMove(exePath, renamedPath);
                BootstrapperHelper.FileMove(tmpDownloadPath, exePath);
                if (!silent)
                    Console.WriteLine("Self update of bootstrapper was successful.");
            }
            catch (Exception)
            {
                if (!silent)
                    Console.WriteLine("Self update failed. Resetting bootstrapper.");
                BootstrapperHelper.FileMove(renamedPath, exePath);
                throw;
            }
        }

    }
}