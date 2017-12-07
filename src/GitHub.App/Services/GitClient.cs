﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using GitHub.Extensions;
using GitHub.Models;
using GitHub.Primitives;
using LibGit2Sharp;
using GitHub.Logging;
using Serilog;

namespace GitHub.Services
{
    [Export(typeof(IGitClient))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class GitClient : IGitClient
    {
        const string defaultOriginName = "origin";
        static readonly ILogger log = LogManager.ForContext<GitClient>();
        readonly PullOptions pullOptions;
        readonly PushOptions pushOptions;
        readonly FetchOptions fetchOptions;

        [ImportingConstructor]
        public GitClient(IGitHubCredentialProvider credentialProvider)
        {
            Guard.ArgumentNotNull(credentialProvider, nameof(credentialProvider));

            pushOptions = new PushOptions { CredentialsProvider = credentialProvider.HandleCredentials };
            fetchOptions = new FetchOptions { CredentialsProvider = credentialProvider.HandleCredentials };
            pullOptions = new PullOptions
            {
                FetchOptions = fetchOptions,
                MergeOptions = new MergeOptions(),
            };
        }

        public Task Pull(IRepository repository)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            return Task.Factory.StartNew(() =>
            {
                var signature = repository.Config.BuildSignature(DateTimeOffset.UtcNow);
#pragma warning disable 0618 // TODO: Replace `Network.Pull` with `Commands.Pull`.
                repository.Network.Pull(signature, pullOptions);
#pragma warning restore 0618
            });
        }

        public Task Push(IRepository repository, string branchName, string remoteName)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(branchName, nameof(branchName));
            Guard.ArgumentNotEmptyString(remoteName, nameof(remoteName));

            return Task.Factory.StartNew(() =>
            {
                if (repository.Head?.Commits != null && repository.Head.Commits.Any())
                {
                    var remote = repository.Network.Remotes[remoteName];
                    var remoteRef = IsCanonical(branchName) ? branchName : @"refs/heads/" + branchName;
                    repository.Network.Push(remote, "HEAD", remoteRef, pushOptions);
                }
            });
        }

        // HACK: This is just a prototype!
        public async Task SyncSubmodules(IRepository repository, Action<string> progress = null)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));

            var workingDir = repository.Info.WorkingDirectory;
            var script =
@"git submodule init
git submodule sync --recursive
git submodule update --recursive";
            var scriptFile = Path.Combine(Path.GetTempPath(), "SyncSubmodules.cmd");
            File.WriteAllText(scriptFile, script);

            var startInfo = new ProcessStartInfo(scriptFile)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using (var process = Process.Start(startInfo))
            {
                var outputReader = process.StandardOutput;

                string line;
                while ((line = await outputReader.ReadLineAsync()) != null)
                {
                    progress?.Invoke(line);
                }
            }
        }

        public Task Fetch(IRepository repository, string remoteName)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(remoteName, nameof(remoteName));

            return Task.Factory.StartNew(() =>
            {
                try
                {
                    var remote = repository.Network.Remotes[remoteName];
#pragma warning disable 0618 // TODO: Replace `Network.Fetch` with `Commands.Fetch`.
                    repository.Network.Fetch(remote, fetchOptions);
#pragma warning restore 0618
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Failed to fetch");
#if DEBUG
                    throw;
#endif
                }
            });
        }

        public Task Fetch(IRepository repo, UriString cloneUrl, params string[] refspecs)
        {
            var httpsUrl = UriString.ToUriString(cloneUrl.ToRepositoryUrl());

            var originRemote = repo.Network.Remotes[defaultOriginName];
            if (originRemote != null && originRemote.Url == httpsUrl)
            {
                return Fetch(repo, defaultOriginName, refspecs);
            }

            return Task.Factory.StartNew(() =>
            {
                try
                {
                    var tempRemoteName = cloneUrl.Owner + "-" + Guid.NewGuid();
                    var remote = repo.Network.Remotes.Add(tempRemoteName, httpsUrl);
                    try
                    {
#pragma warning disable 0618 // TODO: Replace `Network.Fetch` with `Commands.Fetch`.
                        repo.Network.Fetch(remote, refspecs, fetchOptions);
#pragma warning restore 0618
                    }
                    finally
                    {
                        repo.Network.Remotes.Remove(tempRemoteName);
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Failed to fetch");
#if DEBUG
                    throw;
#endif
                }
            });
        }

        public Task Fetch(IRepository repository, string remoteName, params string[] refspecs)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(remoteName, nameof(remoteName));

            return Task.Factory.StartNew(() =>
            {
                try
                {
                    var remote = repository.Network.Remotes[remoteName];
#pragma warning disable 0618 // TODO: Replace `Network.Fetch` with `Commands.Fetch`.
                    repository.Network.Fetch(remote, refspecs, fetchOptions);
#pragma warning restore 0618
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Failed to fetch");
#if DEBUG
                    throw;
#endif
                }
            });
        }

        public Task Checkout(IRepository repository, string branchName)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(branchName, nameof(branchName));

            return Task.Factory.StartNew(() =>
            {
#pragma warning disable 0618 // TODO: Replace `IRepository.Checkout` with `Commands.Checkout`.
                repository.Checkout(branchName);
#pragma warning restore 0618
            });
        }

        public Task CreateBranch(IRepository repository, string branchName)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(branchName, nameof(branchName));

            return Task.Factory.StartNew(() =>
            {
                repository.CreateBranch(branchName);
            });
        }

        public Task<TreeChanges> Compare(
            IRepository repository,
            string sha1,
            string sha2,
            bool detectRenames)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(sha1, nameof(sha1));
            Guard.ArgumentNotEmptyString(sha2, nameof(sha2));

            return Task.Factory.StartNew(() =>
            {
                var options = new CompareOptions
                {
                    Similarity = detectRenames ? SimilarityOptions.Renames : SimilarityOptions.None
                };

                var commit1 = repository.Lookup<Commit>(sha1);
                var commit2 = repository.Lookup<Commit>(sha2);

                if (commit1 != null && commit2 != null)
                {
                    return repository.Diff.Compare<TreeChanges>(commit1.Tree, commit2.Tree, options);
                }
                else
                {
                    return null;
                }
            });
        }

        public Task<Patch> Compare(
            IRepository repository,
            string sha1,
            string sha2,
            string path)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(sha1, nameof(sha1));
            Guard.ArgumentNotEmptyString(sha2, nameof(sha2));
            Guard.ArgumentNotEmptyString(path, nameof(path));

            return Task.Factory.StartNew(() =>
            {
                var commit1 = repository.Lookup<Commit>(sha1);
                var commit2 = repository.Lookup<Commit>(sha2);

                if (commit1 != null && commit2 != null)
                {
                    return repository.Diff.Compare<Patch>(
                        commit1.Tree,
                        commit2.Tree,
                        new[] { path });
                }
                else
                {
                    return null;
                }
            });
        }

        public Task<ContentChanges> CompareWith(IRepository repository, string sha1, string sha2, string path, byte[] contents)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(sha1, nameof(sha1));
            Guard.ArgumentNotEmptyString(sha2, nameof(sha1));
            Guard.ArgumentNotEmptyString(path, nameof(path));

            return Task.Factory.StartNew(() =>
            {
                var commit1 = repository.Lookup<Commit>(sha1);
                var commit2 = repository.Lookup<Commit>(sha2);

                var treeChanges = repository.Diff.Compare<TreeChanges>(commit1.Tree, commit2.Tree);
                var normalizedPath = path.Replace("/", "\\");
                var renamed = treeChanges.FirstOrDefault(x => x.Path == normalizedPath);
                var oldPath = renamed?.OldPath ?? path;

                if (commit1 != null)
                {
                    var contentStream = contents != null ? new MemoryStream(contents) : new MemoryStream();
                    var blob1 = commit1[oldPath]?.Target as Blob ?? repository.ObjectDatabase.CreateBlob(new MemoryStream());
                    var blob2 = repository.ObjectDatabase.CreateBlob(contentStream, path);
                    return repository.Diff.Compare(blob1, blob2);
                }

                return null;
            });
        }

        public Task<T> GetConfig<T>(IRepository repository, string key)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(key, nameof(key));

            return Task.Factory.StartNew(() =>
            {
                var result = repository.Config.Get<T>(key);
                return result != null ? result.Value : default(T);
            });
        }

        public Task SetConfig(IRepository repository, string key, string value)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(key, nameof(key));
            Guard.ArgumentNotEmptyString(value, nameof(value));

            return Task.Factory.StartNew(() =>
            {
                repository.Config.Set(key, value);
            });
        }

        public Task SetRemote(IRepository repository, string remoteName, Uri url)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(remoteName, nameof(remoteName));

            return Task.Factory.StartNew(() =>
            {
                repository.Config.Set("remote." + remoteName + ".url", url.ToString());
                repository.Config.Set("remote." + remoteName + ".fetch", "+refs/heads/*:refs/remotes/" + remoteName + "/*");
            });
        }

        public Task SetTrackingBranch(IRepository repository, string branchName, string remoteName)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(branchName, nameof(branchName));
            Guard.ArgumentNotEmptyString(remoteName, nameof(remoteName));

            return Task.Factory.StartNew(() =>
            {
                var remoteBranchName = IsCanonical(remoteName) ? remoteName : "refs/remotes/" + remoteName + "/" + branchName;
                var remoteBranch = repository.Branches[remoteBranchName];
                // if it's null, it's because nothing was pushed
                if (remoteBranch != null)
                {
                    var localBranchName = IsCanonical(branchName) ? branchName : "refs/heads/" + branchName;
                    var localBranch = repository.Branches[localBranchName];
                    repository.Branches.Update(localBranch, b => b.TrackedBranch = remoteBranch.CanonicalName);
                }
            });
        }

        public Task UnsetConfig(IRepository repository, string key)
        {
            Guard.ArgumentNotEmptyString(key, nameof(key));

            return Task.Factory.StartNew(() =>
            {
                repository.Config.Unset(key);
            });
        }

        public Task<Remote> GetHttpRemote(IRepository repo, string remote)
        {
            Guard.ArgumentNotNull(repo, nameof(repo));
            Guard.ArgumentNotEmptyString(remote, nameof(remote));

            return Task.Factory.StartNew(() =>
            {
                var uri = GitService.GitServiceHelper.GetRemoteUri(repo, remote);
                var remoteName = uri.IsHypertextTransferProtocol ? remote : remote + "-http";
                var ret = repo.Network.Remotes[remoteName];
                if (ret == null)
                    ret = repo.Network.Remotes.Add(remoteName, UriString.ToUriString(uri.ToRepositoryUrl()));
                return ret;
            });
        }

        public Task<string> ExtractFile(IRepository repository, string commitSha, string fileName)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(commitSha, nameof(commitSha));
            Guard.ArgumentNotEmptyString(fileName, nameof(fileName));

            return Task.Factory.StartNew(() =>
            {
                var commit = repository.Lookup<Commit>(commitSha);
                if (commit == null)
                {
                    throw new FileNotFoundException("Couldn't find '" + fileName + "' at commit " + commitSha + ".");
                }

                var blob = commit[fileName]?.Target as Blob;
                return blob?.GetContentText();
            });
        }

        public Task<byte[]> ExtractFileBinary(IRepository repository, string commitSha, string fileName)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(commitSha, nameof(commitSha));
            Guard.ArgumentNotEmptyString(fileName, nameof(fileName));

            return Task.Factory.StartNew(() =>
            {
                var commit = repository.Lookup<Commit>(commitSha);
                if (commit == null)
                {
                    throw new FileNotFoundException("Couldn't find '" + fileName + "' at commit " + commitSha + ".");
                }

                var blob = commit[fileName]?.Target as Blob;

                if (blob != null)
                {
                    using (var m = new MemoryStream())
                    {
                        var content = blob.GetContentStream();
                        content.CopyTo(m);
                        return m.ToArray();
                    }
                }

                return null;
            });
        }

        public Task<bool> IsModified(IRepository repository, string path, byte[] contents)
        {
            Guard.ArgumentNotNull(repository, nameof(repository));
            Guard.ArgumentNotEmptyString(path, nameof(path));

            return Task.Factory.StartNew(() =>
            {
                if (repository.RetrieveStatus(path) == FileStatus.Unaltered)
                {
                    var treeEntry = repository.Head[path];
                    if (treeEntry?.TargetType != TreeEntryTargetType.Blob)
                    {
                        return false;
                    }

                    var blob1 = (Blob)treeEntry.Target;
                    using (var s = contents != null ? new MemoryStream(contents) : new MemoryStream())
                    {
                        var blob2 = repository.ObjectDatabase.CreateBlob(s, path);
                        var diff = repository.Diff.Compare(blob1, blob2);
                        return diff.LinesAdded != 0 || diff.LinesDeleted != 0;
                    }
                }

                return true;
            });
        }

        public async Task<string> GetPullRequestMergeBase(IRepository repo,
            UriString targetCloneUrl, string baseSha, string headSha, string baseRef, int pullNumber)
        {
            Guard.ArgumentNotNull(repo, nameof(repo));
            Guard.ArgumentNotNull(targetCloneUrl, nameof(targetCloneUrl));
            Guard.ArgumentNotEmptyString(baseRef, nameof(baseRef));

            var headCommit = repo.Lookup<Commit>(headSha);
            if (headCommit == null)
            {
                // The PR base branch might no longer exist, so we fetch using `refs/pull/<PR>/head` first.
                // This will often fetch the base commits, even when the base branch no longer exists.
                var headRef = $"refs/pull/{pullNumber}/head";
                await Fetch(repo, targetCloneUrl, headRef);
                headCommit = repo.Lookup<Commit>(headSha);
                if (headCommit == null)
                {
                    throw new NotFoundException($"Couldn't find {headSha} after fetching from {targetCloneUrl}:{headRef}.");
                }
            }

            var baseCommit = repo.Lookup<Commit>(baseSha);
            if (baseCommit == null)
            {
                await Fetch(repo, targetCloneUrl, baseRef);
                baseCommit = repo.Lookup<Commit>(baseSha);
                if (baseCommit == null)
                {
                    throw new NotFoundException($"Couldn't find {baseSha} after fetching from {targetCloneUrl}:{baseRef}.");
                }
            }

            var mergeBaseCommit = repo.ObjectDatabase.FindMergeBase(baseCommit, headCommit);
            if (mergeBaseCommit == null)
            {
                throw new NotFoundException($"Couldn't find merge base between {baseCommit} and {headCommit}.");
            }

            return mergeBaseCommit.Sha;
        }

        public Task<bool> IsHeadPushed(IRepository repo)
        {
            Guard.ArgumentNotNull(repo, nameof(repo));

            return Task.Factory.StartNew(() =>
            {
                return repo.Head.TrackingDetails.AheadBy == 0;
            });
        }

        public Task<IReadOnlyList<CommitMessage>> GetMessagesForUniqueCommits(
            IRepository repo,
            string baseBranch,
            string compareBranch,
            int maxCommits)
        {
            return Task.Factory.StartNew(() =>
            {
                var baseCommit = repo.Lookup<Commit>(baseBranch);
                var compareCommit = repo.Lookup<Commit>(compareBranch);
                if (baseCommit == null || compareCommit == null)
                {
                    var missingBranch = baseCommit == null ? baseBranch : compareBranch;
                    throw new NotFoundException(missingBranch);
                }

                var mergeCommit = repo.ObjectDatabase.FindMergeBase(baseCommit, compareCommit);
                var commitFilter = new CommitFilter
                {
                    IncludeReachableFrom = baseCommit,
                    ExcludeReachableFrom = mergeCommit,
                };

                var commits = repo.Commits
                    .QueryBy(commitFilter)
                    .Take(maxCommits)
                    .Select(c => new CommitMessage(c.Message))
                    .ToList();

                return (IReadOnlyList<CommitMessage>)commits;
            });
        }

        static bool IsCanonical(string s)
        {
            Guard.ArgumentNotEmptyString(s, nameof(s));

            return s.StartsWith("refs/", StringComparison.Ordinal);
        }
    }
}
