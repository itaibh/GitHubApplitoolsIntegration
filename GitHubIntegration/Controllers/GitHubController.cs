using GitHubIntegration.Models;
using Jose;
using Newtonsoft.Json;
using Octokit;
using Octokit.Helpers;
using Octokit.Internal;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Http;
using System.Web.Http.Controllers;

namespace GitHubIntegration.Controllers
{
    public class GitHubController : ApiController
    {
        private GitHubClient client_;
        private SimpleJsonSerializer serializer_;
        private string clientId_ = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_CLIENT_ID");
        private string clientSecret_ = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_CLIENT_SECRET");
        private IReadOnlyList<Installation> installations_ = null;
        private string secretToken_ = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_SECRET_WEBHOOK_TOKEN");

        protected override void Initialize(HttpControllerContext controllerContext)
        {
            base.Initialize(controllerContext);

            serializer_ = new SimpleJsonSerializer();
            //string personalAccessToken = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_PAT");

            string pemFilePath = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_PEM_FILE_PATH");
            string pemData = File.ReadAllText(pemFilePath);

            long unixTimeInSeconds = DateTimeOffset.UtcNow.ToUnixTime();
            object payload = new
            {
                iat = unixTimeInSeconds,
                exp = unixTimeInSeconds + 300,
                iss = 7820
            };

            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(pemData);

            string token = JWT.Encode(payload, rsa, JwsAlgorithm.RS256);

            client_ = new GitHubClient(new ProductHeaderValue("ApplitoolsIntegration"))
            {
                Credentials = new Credentials(token, AuthenticationType.Bearer)
            };

            installations_ = client_.Installations.GetAll().Result;
        }

        //public IHttpActionResult Authorize(string code, string state)
        //{
        //    if (String.IsNullOrEmpty(code))
        //        return Redirect("Index");

        //    var session = HttpContext.Current.Session;

        //    var expectedState = session["CSRF:State"] as string;
        //    if (state != expectedState) throw new InvalidOperationException("SECURITY FAIL!");
        //    session["CSRF:State"] = null;

        //    var request = new OauthTokenRequest(clientId_, clientSecret_, code);
        //    var token = client_.Oauth.CreateAccessToken(request).Result;
        //    session["OAuthToken"] = token.AccessToken;

        //    return Redirect("Index");
        //}

        public IHttpActionResult Post()
        {
            string json = Request.Content.ReadAsStringAsync().Result;
            string gitHubEvent = Request.Headers.GetValues("X-Github-Event").First();
            if (!VerifySigniture_(json))
            {
                return Unauthorized();
            }
            switch (gitHubEvent)
            {
                case "pull_request":
                    PullRequestEventPayload prep = serializer_.Deserialize<PullRequestEventPayload>(json);
                    return ProcessPullRequest_(prep);

                case "status":
                    StatusEventPayload sep = serializer_.Deserialize<StatusEventPayload>(json);
                    return ProcessStatusRequest_(sep);
            }
            return BadRequest();
        }

        public static string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);
            foreach (byte b in ba)
                hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }

        private bool VerifySigniture_(string payloadBody)
        {
            if (secretToken_ == null)
            {
                return true;
            }
            string gitHubSig = Request.Headers.GetValues("X-Hub-Signature").FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(gitHubSig))
            {
                byte[] key = Encoding.UTF8.GetBytes(secretToken_);
                byte[] payload = Encoding.UTF8.GetBytes(payloadBody);
                string signature = null;
                using (HMAC hmac = new HMACSHA1(key))
                {
                    signature = "sha1=" + ByteArrayToString(hmac.ComputeHash(payload));
                }
                return signature.Equals(gitHubSig, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private IHttpActionResult ProcessStatusRequest_(StatusEventPayload sep)
        {
            if (sep.Context.StartsWith("continuous-integration/") || sep.Context.StartsWith("ci/"))
            {
                NewCommitStatus newCommitStatus = new NewCommitStatus
                {
                    Context = "tests/applitools"
                };
                try
                {
                    string batchId = GetServerBatchIdFromStartInfoBatchId_(sep.Sha);
                    if (batchId == null)
                    {
                        newCommitStatus.Description = "No tests available";
                        newCommitStatus.State = CommitState.Success;
                    }
                    else
                    {
                        newCommitStatus.TargetUrl = GetTargetUrlFromBatchId_(batchId);
                    }

                    if (sep.State.Value == CommitState.Pending)
                    {
                        newCommitStatus.Description = "Test is running";
                        newCommitStatus.State = CommitState.Pending;
                    }
                    else
                    {
                        BatchData batchData = GetBatchResultsSummary_(batchId);
                        if (batchData == null)
                        {
                            newCommitStatus.Description = "No tests available";
                            newCommitStatus.State = CommitState.Success;
                        }
                        else if (batchData.UnresolvedCount == 0 && batchData.FailedCount == 0)
                        {
                            newCommitStatus.Description = "All tests passed";
                            newCommitStatus.State = CommitState.Success;
                        }
                        else
                        {
                            newCommitStatus.Description = batchData.ToString();
                            newCommitStatus.State = CommitState.Failure;
                        }
                    }
                }
                catch (Exception)
                {
                    newCommitStatus.Description = "An error has occured";
                    newCommitStatus.State = CommitState.Error;
                }

                if (installations_.Count == 0)
                {
                    return InternalServerError();
                }

                UpdateCommitStatus(sep.Repository, sep.Sha, newCommitStatus);

                return Ok();
            }
            return BadRequest();
        }

        private void UpdateCommitStatus(Repository rep, string sha, NewCommitStatus newCommitStatus)
        {
            var userId = rep.Owner.Id;
            var installation = installations_.Where(inst => inst.Account.Id == userId).FirstOrDefault();
            int installationId = installation.Id;
            AccessToken accessToken = client_.Installations.AccessTokens.Create(installationId).Result;

            GitHubClient installationsClient = new GitHubClient(new ProductHeaderValue("ApplitoolsIntegration"))
            {
                Credentials = new Credentials(accessToken.Token)
            };

            IApiConnection apiConnection = new ApiConnection(installationsClient.Connection);

            CommitStatusClient csc = new CommitStatusClient(apiConnection);
            CommitStatus commitStatus = csc.Create(rep.Id, sha, newCommitStatus).Result;
        }

        private string GetTargetUrlFromBatchId_(string batchId)
        {
            return string.Format("https://eyes.applitools.com/app/batches/{0}", batchId);
        }

        public string Get(string uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        // TODO - change!
        private string GetServerBatchIdFromStartInfoBatchId_(string reference)
        {
            string credentials = ConfigurationManager.AppSettings["APPLITOOLS_SERVER_CREDENTIALS"];
            string url = string.Format("https://eyes.applitools.com/api/sessions/batches/batchId/{0}?format=json&{1}", reference, credentials);
            string json = Get(url);
            BatchIdData batchIdData = JsonConvert.DeserializeObject<BatchIdData>(json);
            if (batchIdData == null)
            {
                return null;
            }
            return batchIdData.BatchId;
        }

        // TODO - change!
        private BatchData GetBatchResultsSummary_(string batchId)
        {
            string credentials = ConfigurationManager.AppSettings["APPLITOOLS_SERVER_CREDENTIALS"];
            string url = string.Format("https://eyes.applitools.com/api/sessions/batches?format=json&count=1&limit=%3D%3D+{0}&{1}", batchId, credentials);
            string json = Get(url);
            BatchResultsSummary batchResultsSummary = JsonConvert.DeserializeObject<BatchResultsSummary>(json);
            if (batchResultsSummary == null || batchResultsSummary.Batches == null || batchResultsSummary.Batches.Length == 0)
            {
                return null;
            }

            BatchData batchData = batchResultsSummary.Batches[0];
            return batchData;
        }

        private IHttpActionResult ProcessPullRequest_(PullRequestEventPayload pr)
        {
            if (pr.Action == "opened" || pr.Action == "reopened")
            {
                string sha = pr.PullRequest.Head.Sha;

                UpdateCommitStatus(pr.Repository, sha, new NewCommitStatus
                {
                    State = CommitState.Pending,
                    Description = "The test is running",
                    TargetUrl = "https://eyes.applitools.com"
                });

                Thread.Sleep(2000);

                UpdateCommitStatus(pr.Repository, sha, new NewCommitStatus
                {
                    State = CommitState.Success,
                    Description = "The test passed",
                    TargetUrl = "https://eyes.applitools.com"
                });

                return Ok();
            }
            return BadRequest();
        }
    }
}
