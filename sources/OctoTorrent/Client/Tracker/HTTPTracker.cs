//
// HTTPTracker.cs
//
// Authors:
//   Eric Butler eric@extremeboredom.net
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2007 Eric Butler
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

namespace OctoTorrent.Client.Tracker
{
    using System;
    using System.Collections.Generic;
    using BEncoding;
    using System.Text.RegularExpressions;
    using System.Net;
    using Common;
    using System.IO;

    public class HTTPTracker : Tracker
    {
        private static readonly Random Random = new Random();
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly Uri _scrapeUrl;
        private string _trackerId;

        public string Key { get; private set; }

        public Uri ScrapeUri
        {
            get { return _scrapeUrl; }
        }

        public HTTPTracker(Uri announceUrl)
            : base(announceUrl)
        {
            CanAnnounce = true;
            var index = announceUrl.OriginalString.LastIndexOf('/');
            var part = (index + 9 <= announceUrl.OriginalString.Length) ? announceUrl.OriginalString.Substring(index + 1, 8) : "";
            if (part.Equals("announce", StringComparison.OrdinalIgnoreCase))
            {
                CanScrape = true;
                var r = new Regex("announce");
                _scrapeUrl = new Uri(r.Replace(announceUrl.OriginalString, "scrape", 1, index));
            }

            var passwordKey = new byte[8];
            lock (Random)
                Random.NextBytes(passwordKey);
            Key = UriHelper.UrlEncode(passwordKey);
        }

        public override void Announce(AnnounceParameters parameters, object state)
        {
            try
            {
                var announceString = CreateAnnounceString(parameters);
                var request = (HttpWebRequest) WebRequest.Create(announceString);
                request.UserAgent = VersionInfo.ClientVersion;
                request.Proxy = new WebProxy();   // If i don't do this, i can't run the webrequest. It's wierd.
                RaiseBeforeAnnounce();
                BeginRequest(request, AnnounceReceived, new[] { request, state });
            }
            catch (Exception ex)
            {
                Status = TrackerState.Offline;
                FailureMessage = ("Could not initiate announce request: " + ex.Message);
                RaiseAnnounceComplete(new AnnounceResponseEventArgs(this, state, false));
            }
        }

        private void BeginRequest(WebRequest request, AsyncCallback callback, object state)
        {
            var result = request.BeginGetResponse(callback, state);
            ClientEngine.MainLoop.QueueTimeout(RequestTimeout, () =>
                                                                   {
                                                                       if (!result.IsCompleted)
                                                                           request.Abort();
                                                                       return false;
                                                                   });
        }

        void AnnounceReceived(IAsyncResult result)
        {
            FailureMessage = string.Empty;
            WarningMessage = string.Empty;
            var stateOb = (object[])result.AsyncState;
            var request = (WebRequest)stateOb[0];
            var state = stateOb[1];
            var peers = new List<Peer>();
            try
            {
                var dict = DecodeResponse(request, result);
                HandleAnnounce(dict, peers);
                Status = TrackerState.Ok;
            }
            catch (WebException)
            {
                Status = TrackerState.Offline;
                FailureMessage = "The tracker could not be contacted";
            }
            catch
            {
                Status = TrackerState.InvalidResponse;
                FailureMessage = "The tracker returned an invalid or incomplete response";
            }
            finally
            {
                RaiseAnnounceComplete(new AnnounceResponseEventArgs(this, state, string.IsNullOrEmpty(FailureMessage), peers));
            }
        }

        private Uri CreateAnnounceString(AnnounceParameters parameters)
        {
            var builder = new UriQueryBuilder(Uri);
            builder.Add("info_hash", parameters.InfoHash.UrlEncode())
                .Add("peer_id", parameters.PeerId)
                .Add("port", parameters.Port)
                .Add("uploaded", parameters.BytesUploaded)
                .Add("downloaded", parameters.BytesDownloaded)
                .Add("left", parameters.BytesLeft)
                .Add("compact", 1)
                .Add("numwant", 100);

            if (parameters.SupportsEncryption)
                builder.Add("supportcrypto", 1);
            if (parameters.RequireEncryption)
                builder.Add("requirecrypto", 1);
            if (!builder.Contains("key"))
                builder.Add("key", Key);
            if (!string.IsNullOrEmpty(parameters.Ipaddress))
                builder.Add("ip", parameters.Ipaddress);

            // If we have not successfully sent the started event to this tier, override the passed in started event
            // Otherwise append the event if it is not "none"
            //if (!parameters.Id.Tracker.Tier.SentStartedEvent)
            //{
            //    sb.Append("&event=started");
            //    parameters.Id.Tracker.Tier.SendingStartedEvent = true;
            //}
            if (parameters.ClientEvent != TorrentEvent.None)
                builder.Add("event", parameters.ClientEvent.ToString().ToLower());

            if (!string.IsNullOrEmpty(_trackerId))
                builder.Add("trackerid", _trackerId);

            return builder.ToUri();
        }

        private BEncodedDictionary DecodeResponse(WebRequest request, IAsyncResult result)
        {
            var totalRead = 0;
            var buffer = new byte[2048];

            var response = request.EndGetResponse(result);
            using (var dataStream = new MemoryStream(response.ContentLength > 0 ? (int)response.ContentLength : 256))
            {
                using (var reader = new BinaryReader(response.GetResponseStream()))
                {
                    // If there is a ContentLength, use that to decide how much we read.
                    int bytesRead;
                    if (response.ContentLength > 0)
                    {
                        while (totalRead < response.ContentLength)
                        {
                            bytesRead = reader.Read(buffer, 0, buffer.Length);
                            dataStream.Write(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                        }
                    }
                    else    // A compact response doesn't always have a content length, so we
                    {       // just have to keep reading until we think we have everything.
                        while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                            dataStream.Write(buffer, 0, bytesRead);
                    }
                }
                response.Close();
                dataStream.Seek(0, SeekOrigin.Begin);
                return (BEncodedDictionary)BEncodedValue.Decode(dataStream);
            }
        }

        public override bool Equals(object obj)
        {
            var tracker = obj as HTTPTracker;
            return tracker != null && (Uri.Equals(tracker.Uri));

            // If the announce URL matches, then CanScrape and the scrape URL must match too
        }

        public override int GetHashCode()
        {
            return Uri.GetHashCode();
        }

        void HandleAnnounce(BEncodedDictionary dict, List<Peer> peers)
        {
            foreach (var keypair in dict)
            {
                switch (keypair.Key.Text)
                {
                    case ("complete"):
                        Complete = Convert.ToInt32(keypair.Value.ToString());
                        break;

                    case ("incomplete"):
                        Incomplete = Convert.ToInt32(keypair.Value.ToString());
                        break;

                    case ("downloaded"):
                        Downloaded = Convert.ToInt32(keypair.Value.ToString());
                        break;

                    case ("tracker id"):
                        _trackerId = keypair.Value.ToString();
                        break;

                    case ("min interval"):
                        MinUpdateInterval = TimeSpan.FromSeconds(int.Parse(keypair.Value.ToString()));
                        break;

                    case ("interval"):
                        UpdateInterval = TimeSpan.FromSeconds(int.Parse(keypair.Value.ToString()));
                        break;

                    case ("peers"):
                        if (keypair.Value is BEncodedList)          // Non-compact response
                            peers.AddRange(Peer.Decode((BEncodedList)keypair.Value));
                        else if (keypair.Value is BEncodedString)   // Compact response
                            peers.AddRange(Peer.Decode((BEncodedString)keypair.Value));
                        break;

                    case ("failure reason"):
                        FailureMessage = keypair.Value.ToString();
                        break;

                    case ("warning message"):
                        WarningMessage = keypair.Value.ToString();
                        break;

                    default:
                        Logger.Log(null, "HttpTracker - Unknown announce tag received: Key {0}  Value: {1}", keypair.Key.ToString(), keypair.Value.ToString());
                        break;
                }
            }
        }

        public override void Scrape(ScrapeParameters parameters, object state)
        {
            try
            {
                var url = _scrapeUrl.OriginalString;

                // If you want to scrape the tracker for *all* torrents, don't append the info_hash.
                if (url.IndexOf('?') == -1)
                    url += "?info_hash=" + parameters.InfoHash.UrlEncode ();
                else
                    url += "&info_hash=" + parameters.InfoHash.UrlEncode ();

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = VersionInfo.ClientVersion;
                BeginRequest(request, ScrapeReceived, new[] { request, state });
            }
            catch
            {
                RaiseScrapeComplete(new ScrapeResponseEventArgs(this, state, false));
            }
        }

        void ScrapeReceived(IAsyncResult result)
        {
            var message = string.Empty;
            var stateOb = (object[])result.AsyncState;
            var request = (WebRequest)stateOb[0];
            var state = stateOb[1];

            try
            {
                var dict = DecodeResponse(request, result);

                // FIXME: Log the failure?
                if (!dict.ContainsKey("files"))
                {
                    message = "Response contained no data";
                    return;
                }
                var files = (BEncodedDictionary)dict["files"];
                foreach (var keypair in files)
                {
                    var d = (BEncodedDictionary) keypair.Value;
                    foreach (var kp in d)
                    {
                        switch (kp.Key.ToString())
                        {
                            case ("complete"):
                                Complete = (int)((BEncodedNumber)kp.Value).Number;
                                break;

                            case ("downloaded"):
                                Downloaded = (int)((BEncodedNumber)kp.Value).Number;
                                break;

                            case ("incomplete"):
                                Incomplete = (int)((BEncodedNumber)kp.Value).Number;
                                break;

                            default:
                                Logger.Log(null, "HttpTracker - Unknown scrape tag received: Key {0}  Value {1}", kp.Key.ToString(), kp.Value.ToString());
                                break;
                        }
                    }
                }
            }
            catch (WebException)
            {
                message = "The tracker could not be contacted";
            }
            catch
            {
                message = "The tracker returned an invalid or incomplete response";
            }
            finally
            {
                RaiseScrapeComplete(new ScrapeResponseEventArgs(this, state, string.IsNullOrEmpty(message)));
            }
        }

        public override string ToString()
        {
            return Uri.ToString();
        }
    }
}
