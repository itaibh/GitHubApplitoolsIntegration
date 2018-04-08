using GitHubIntegration.Models;
using Newtonsoft.Json;
using Octokit;
using Octokit.Internal;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
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
        //private IReadOnlyList<Installation> installations_ = null;
        private string secretToken_ = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_SECRET_WEBHOOK_TOKEN");
        //private int githubApplicationId = 7820;
        private int githubApplicationId_ = 8362;

        private readonly UriBuilder baseBuilder = new UriBuilder() { Scheme = "https", Host = "e195ad92.ngrok.io", Path = "/api/github/" };

        protected override void Initialize(HttpControllerContext controllerContext)
        {
            base.Initialize(controllerContext);

            serializer_ = new SimpleJsonSerializer();

            string personalAccessToken = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_PAT");
            client_ = new GitHubClient(new Octokit.ProductHeaderValue("ApplitoolsIntegration"))
            {
                Credentials = new Credentials(personalAccessToken, AuthenticationType.Oauth)
            };

            /*

            string pemFilePath = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_PEM_FILE_PATH");
            string pemData = File.ReadAllText(pemFilePath);

            long unixTimeInSeconds = DateTimeOffset.UtcNow.ToUnixTime();
            object payload = new
            {
                iat = unixTimeInSeconds,
                exp = unixTimeInSeconds + 300,
                iss = githubApplicationId_
            };

            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(pemData);

            string token = JWT.Encode(payload, rsa, JwsAlgorithm.RS256);

            client_ = new GitHubClient(new ProductHeaderValue("ApplitoolsIntegration"))
            {
                Credentials = new Credentials(token, AuthenticationType.Bearer)
            };
            */

            //installations_ = client_.GitHubApps.GetAllInstallationsForCurrent().Result;
        }

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
                case "push":
                    PushEventPayload pep = serializer_.Deserialize<PushEventPayload>(json);
                    return ProcessPushRequest_(pep);

                case "pull_request":
                    PullRequestEventPayload prep = serializer_.Deserialize<PullRequestEventPayload>(json);
                    return ProcessPullRequest_(prep);

                case "status":
                    StatusEventPayload sep = serializer_.Deserialize<StatusEventPayload>(json);
                    return ProcessStatusRequest_(sep);
            }
            return BadRequest();
        }
        private string GenerateSecurityString()
        {
            using (RandomNumberGenerator rng = new RNGCryptoServiceProvider())
            {
                byte[] tokenData = new byte[32];
                rng.GetBytes(tokenData);

                string token = Convert.ToBase64String(tokenData);
                token = token.Replace('+', '-').Replace('/', '_').Replace('=', '.');
                return token;
            }
        }

        [HttpGet]
        public IHttpActionResult Register()
        {
            string state = GenerateSecurityString();

            HttpContext.Current.Session["CSRF:State"] = state;
            return Redirect(string.Format("https://github.com/login/oauth/authorize?client_id={0}&state={1}", clientId_, state));
        }

        [HttpGet]
        public IHttpActionResult Authorize(string code, string state)
        {
            StringBuilder html = new StringBuilder("<html><head><title>Register with GitHub</title></head>");
            HttpResponseMessage responseMessage = new HttpResponseMessage(HttpStatusCode.OK);

            if (String.IsNullOrEmpty(code))
            {
                html.Append("<body><h1>Error</h1><p>No github oauth app code was given.</p></body></html>");
                responseMessage.Content = new StringContent(html.ToString(), Encoding.UTF8, "text/html");
                return new System.Web.Http.Results.ResponseMessageResult(responseMessage);
            }

            var session = HttpContext.Current.Session;

            var expectedState = session["CSRF:State"] as string;
            if (expectedState != null && state != expectedState) throw new InvalidOperationException("SECURITY FAIL!");
            session["CSRF:State"] = null;

            var request = new OauthTokenRequest(clientId_, clientSecret_, code);
            var token = client_.Oauth.CreateAccessToken(request).Result;
            session["OAuthToken"] = token.AccessToken;

            //1 fetch repositories list
            //2 setup webhook for a repository
            html.AppendLine("<body><h1>Repositories</h1><ul>");
            IReadOnlyList<Repository> repositories = client_.Repository.GetAllForCurrent().Result;
            UriBuilder builder = new UriBuilder(baseBuilder.ToString());
            foreach (Repository r in repositories)
            {
                builder.Path = "/api/github/CreateWebhook";
                builder.Query = "repositoryId=" + r.Id;

                html.Append("<li><a href=\"").Append(builder.Uri).Append("\">").Append(r.Owner.Name ?? r.Owner.Login).Append(" / ").Append(r.Name).AppendLine("</a></li>");
            }
            html.Append("</body></html>");

            responseMessage.Content = new StringContent(html.ToString(), Encoding.UTF8, "text/html");
            return new System.Web.Http.Results.ResponseMessageResult(responseMessage);
        }

        [HttpGet]
        public IHttpActionResult CreateWebhook(long repositoryId)
        {
            HttpResponseMessage responseMessage = new HttpResponseMessage();
            var config = new Dictionary<string, string> { };

            UriBuilder builder = new UriBuilder(baseBuilder.ToString());
            NewRepositoryWebHook hook = new NewRepositoryWebHook("web", config, builder.ToString())
            {
                Active = true,
                Secret = clientSecret_,
                Events = new string[] { "push", "pull_request" },
                ContentType = WebHookContentType.Json
            };

            RepositoryHook webhook = client_.Repository.Hooks.Create(repositoryId, hook).Result;

            return new System.Web.Http.Results.ResponseMessageResult(responseMessage);
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

        private IHttpActionResult ProcessPushRequest_(PushEventPayload pep)
        {
            IApiConnection apiConnection = CreateConnection_(pep);
            IRepositoryBranchesClient branchesClient = new RepositoryBranchesClient(apiConnection);
            IReadOnlyList<Branch> branches = branchesClient.GetAll(pep.Repository.Id).Result;
            Branch parentBranch = GetParentBranch_(pep, apiConnection, branches);

            return Ok();
        }

        private static Branch GetParentBranch_(PushEventPayload pep, IApiConnection apiConnection, IReadOnlyList<Branch> branches)
        {
            var branchesDict = new Dictionary<string, Branch>();
            foreach (Branch branch in branches)
            {
                branchesDict.Add(branch.Commit.Sha, branch);
            }

            IEnumerable<CommitPayload> commits = pep.Commits;
            ICommitsClient commitsClient = new CommitsClient(apiConnection);

            return GetParentBranch_(pep, branchesDict, commits, commitsClient, TimeSpan.FromSeconds(5));
        }

        private static Branch GetParentBranch_(PushEventPayload pep, Dictionary<string, Branch> branchesDict, IEnumerable<CommitPayload> commits, ICommitsClient commitsClient, TimeSpan timeout)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CommitPayload commitPayload = commits.Last();
            string commitSha = commitPayload.Id;
            Commit commit = commitsClient.Get(pep.Repository.Id, commitSha).Result;

            Queue<GitReference> parentGitReferences = new Queue<GitReference>();
            HashSet<GitReference> parentsHashSet = new HashSet<GitReference>();
            if (commit.Parents != null)
            {
                foreach (GitReference gitRef in commit.Parents)
                {
                    if (!parentsHashSet.Contains(gitRef))
                    {
                        parentGitReferences.Enqueue(gitRef);
                        parentsHashSet.Add(gitRef);
                    }
                }
            }

            while (parentGitReferences.Count > 0 /*&& stopwatch.Elapsed < timeout*/)
            {
                GitReference gitRef = parentGitReferences.Dequeue();
                Commit gitCommit = commitsClient.Get(pep.Repository.Id, gitRef.Sha).Result;

                if (branchesDict.TryGetValue(gitCommit.Sha, out Branch branch) && branch != null)
                {
                    return branch;
                }

                foreach (GitReference parent in gitCommit.Parents)
                {
                    parentGitReferences.Enqueue(parent);
                }
            }

            return null;
        }

        private IHttpActionResult ProcessStatusRequest_(StatusEventPayload sep)
        {
            if (sep.Context.StartsWith("continuous-integration/") || sep.Context.StartsWith("ci/") || sep.Context == "semaphoreci")
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
                /*
                if (installations_.Count == 0)
                {
                    return InternalServerError();
                }
                */
                UpdateCommitStatus(sep.Repository, sep.Sha, newCommitStatus, sep);

                return Ok();
            }
            return BadRequest();
        }

        private void UpdateCommitStatus(Repository rep, string sha, NewCommitStatus newCommitStatus, ActivityPayload payload)
        {
            /*if (!installationId.HasValue)
            {
                var userId = rep.Owner.Id;
                var installation = installations_.Where(inst => inst.Account.Id == userId).FirstOrDefault();
                installationId = installation.Id;
            }*/

            //AccessToken accessToken = payload.Installation.CreateAccessToken(client_).Result;
            IApiConnection apiConnection = CreateConnection_(payload);

            CommitStatusClient csc = new CommitStatusClient(apiConnection);
            CommitStatus commitStatus = csc.Create(rep.Id, sha, newCommitStatus).Result;
        }

        private IApiConnection CreateConnection_(ActivityPayload payload)
        {
            IGitHubClient client;
            if (payload.Installation != null)
            {
                AccessToken accessToken = client_.GitHubApps.CreateInstallationToken(payload.Installation.Id).Result;

                GitHubClient installationsClient = new GitHubClient(new Octokit.ProductHeaderValue("ApplitoolsIntegration"))
                {
                    Credentials = new Credentials(accessToken.Token)
                };

                client = installationsClient;
            }
            else
            {
                client = client_;
            }
            IApiConnection apiConnection = new ApiConnection(client.Connection);
            return apiConnection;
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
            //long installationId = pr.Installation.Id;

            if (pr.Action == "opened" || pr.Action == "reopened")
            {
                string sha = pr.PullRequest.Head.Sha;

                UpdateCommitStatus(pr.Repository, sha, new NewCommitStatus
                {
                    State = CommitState.Pending,
                    Description = "The test is running",
                    TargetUrl = "https://eyes.applitools.com"
                },
                pr);

                Thread.Sleep(2000);

                UpdateCommitStatus(pr.Repository, sha, new NewCommitStatus
                {
                    State = CommitState.Success,
                    Description = "The test passed",
                    TargetUrl = "https://eyes.applitools.com"
                },
                pr);

                return Ok();
            }
            else if (pr.Action == "closed" && pr.PullRequest.Merged)
            {
                string mergeSha = pr.PullRequest.MergeCommitSha;
                GitReference baseRef = pr.PullRequest.Base;
                GitReference headRef = pr.PullRequest.Head;

                string sha = headRef.Sha;

                UpdateCommitStatus(pr.Repository, sha, new NewCommitStatus
                {
                    State = CommitState.Failure,
                    Description = "The test branch failed to merge",
                    TargetUrl = "https://eyes.applitools.com"
                },
                pr);

                return Ok();
            }
            return BadRequest();
        }
    }
}
