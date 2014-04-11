/*
This code is derived from jgit (http://eclipse.org/jgit).
Copyright owners are documented in jgit's IP log.

This program and the accompanying materials are made available
under the terms of the Eclipse Distribution License v1.0 which
accompanies this distribution, is reproduced below, and is
available at http://www.eclipse.org/org/documents/edl-v10.php

All rights reserved.

Redistribution and use in source and binary forms, with or
without modification, are permitted provided that the following
conditions are met:

- Redistributions of source code must retain the above copyright
  notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above
  copyright notice, this list of conditions and the following
  disclaimer in the documentation and/or other materials provided
  with the distribution.

- Neither the name of the Eclipse Foundation, Inc. nor the
  names of its contributors may be used to endorse or promote
  products derived from this software without specific prior
  written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NGit;
using NGit.Api;
using NGit.Api.Errors;
using NGit.Dircache;
using NGit.Internal;
using NGit.Merge;
using NGit.Revwalk;
using NGit.Treewalk;
using Sharpen;

namespace NGit.Api
{
	/// <summary>
	/// A class used to execute a
	/// <code>Merge</code>
	/// command. It has setters for all
	/// supported options and arguments of this command and a
	/// <see cref="Call()">Call()</see>
	/// method
	/// to finally execute the command. Each instance of this class should only be
	/// used for one invocation of the command (means: one call to
	/// <see cref="Call()">Call()</see>
	/// )
	/// </summary>
	/// <seealso><a href="http://www.kernel.org/pub/software/scm/git/docs/git-merge.html"
	/// *      >Git documentation about Merge</a></seealso>
	public class MergeCommand : GitCommand<MergeCommandResult>
	{
		private MergeStrategy mergeStrategy = MergeStrategy.RESOLVE;

		private IList<Ref> commits = new List<Ref>();

		private bool squash;

	    private Func<string, int> _mergeFilter;

	    /// <param name="repo"></param>
		protected internal MergeCommand(Repository repo) : base(repo)
		{
		}

		/// <summary>
		/// Executes the
		/// <code>Merge</code>
		/// command with all the options and parameters
		/// collected by the setter methods (e.g.
		/// <see cref="Include(NGit.Ref)">Include(NGit.Ref)</see>
		/// ) of this
		/// class. Each instance of this class should only be used for one invocation
		/// of the command. Don't call this method twice on an instance.
		/// </summary>
		/// <returns>the result of the merge</returns>
		/// <exception cref="NGit.Api.Errors.GitAPIException"></exception>
		/// <exception cref="NGit.Api.Errors.NoHeadException"></exception>
		/// <exception cref="NGit.Api.Errors.ConcurrentRefUpdateException"></exception>
		/// <exception cref="NGit.Api.Errors.CheckoutConflictException"></exception>
		/// <exception cref="NGit.Api.Errors.InvalidMergeHeadsException"></exception>
		/// <exception cref="NGit.Api.Errors.WrongRepositoryStateException"></exception>
		/// <exception cref="NGit.Api.Errors.NoMessageException"></exception>
		public override MergeCommandResult Call()
		{
			CheckCallable();
			if (commits.Count != 1)
			{
				throw new InvalidMergeHeadsException(commits.IsEmpty() ? JGitText.Get().noMergeHeadSpecified
					 : MessageFormat.Format(JGitText.Get().mergeStrategyDoesNotSupportHeads, mergeStrategy
					.GetName(), Sharpen.Extensions.ValueOf(commits.Count)));
			}
			RevWalk revWalk = null;
			DirCacheCheckout dco = null;
			try
			{
				Ref head = repo.GetRef(Constants.HEAD);
				if (head == null)
				{
					throw new NoHeadException(JGitText.Get().commitOnRepoWithoutHEADCurrentlyNotSupported
						);
				}
				StringBuilder refLogMessage = new StringBuilder("merge ");
				// Check for FAST_FORWARD, ALREADY_UP_TO_DATE
				revWalk = new RevWalk(repo);
				// we know for now there is only one commit
				Ref @ref = commits[0];
				refLogMessage.Append(@ref.GetName());
				// handle annotated tags
				ObjectId objectId = @ref.GetPeeledObjectId();
				if (objectId == null)
				{
					objectId = @ref.GetObjectId();
				}
				RevCommit srcCommit = revWalk.LookupCommit(objectId);
				ObjectId headId = head.GetObjectId();
				if (headId == null)
				{
					revWalk.ParseHeaders(srcCommit);
					dco = new DirCacheCheckout(repo, repo.LockDirCache(), srcCommit.Tree);
					dco.SetFailOnConflict(true);
					dco.Checkout();
					RefUpdate refUpdate = repo.UpdateRef(head.GetTarget().GetName());
					refUpdate.SetNewObjectId(objectId);
					refUpdate.SetExpectedOldObjectId(null);
					refUpdate.SetRefLogMessage("initial pull", false);
					if (refUpdate.Update() != RefUpdate.Result.NEW)
					{
						throw new NoHeadException(JGitText.Get().commitOnRepoWithoutHEADCurrentlyNotSupported
							);
					}
					SetCallable(false);
					return new MergeCommandResult(srcCommit, srcCommit, new ObjectId[] { null, srcCommit
						 }, MergeStatus.FAST_FORWARD, mergeStrategy, null, null);
				}
				RevCommit headCommit = revWalk.LookupCommit(headId);
				if (revWalk.IsMergedInto(srcCommit, headCommit))
				{
					SetCallable(false);
					return new MergeCommandResult(headCommit, srcCommit, new ObjectId[] { headCommit, 
						srcCommit }, MergeStatus.ALREADY_UP_TO_DATE, mergeStrategy, null, null);
				}
				else
				{
					if (revWalk.IsMergedInto(headCommit, srcCommit))
					{
						// FAST_FORWARD detected: skip doing a real merge but only
						// update HEAD
						refLogMessage.Append(": " + MergeStatus.FAST_FORWARD);
						dco = new DirCacheCheckout(repo, headCommit.Tree, repo.LockDirCache(), srcCommit.
							Tree);
						dco.SetFailOnConflict(true);
						dco.Checkout();
						string msg = null;
						ObjectId newHead;
						ObjectId @base = null;
						MergeStatus mergeStatus = null;
						if (!squash)
						{
							UpdateHead(refLogMessage, srcCommit, headId);
							newHead = @base = srcCommit;
							mergeStatus = MergeStatus.FAST_FORWARD;
						}
						else
						{
							msg = JGitText.Get().squashCommitNotUpdatingHEAD;
							newHead = @base = headId;
							mergeStatus = MergeStatus.FAST_FORWARD_SQUASHED;
							IList<RevCommit> squashedCommits = RevWalkUtils.Find(revWalk, srcCommit, headCommit
								);
							string squashMessage = new SquashMessageFormatter().Format(squashedCommits, head);
							repo.WriteSquashCommitMsg(squashMessage);
						}
						SetCallable(false);
						return new MergeCommandResult(newHead, @base, new ObjectId[] { headCommit, srcCommit
							 }, mergeStatus, mergeStrategy, null, msg);
					}
					else
					{
						string mergeMessage = string.Empty;
						if (!squash)
						{
							mergeMessage = new MergeMessageFormatter().Format(commits, head);
							repo.WriteMergeCommitMsg(mergeMessage);
							repo.WriteMergeHeads(Arrays.AsList(@ref.GetObjectId()));
						}
						else
						{
							IList<RevCommit> squashedCommits = RevWalkUtils.Find(revWalk, srcCommit, headCommit
								);
							string squashMessage = new SquashMessageFormatter().Format(squashedCommits, head);
							repo.WriteSquashCommitMsg(squashMessage);
						}
						Merger merger = mergeStrategy.NewMerger(repo);
						bool noProblems;
						IDictionary<string, MergeResult<NGit.Diff.Sequence>> lowLevelResults = null;
						IDictionary<string, ResolveMerger.MergeFailureReason> failingPaths = null;
						IList<string> unmergedPaths = null;
						if (merger is ResolveMerger)
						{
							ResolveMerger resolveMerger = (ResolveMerger)merger;
							resolveMerger.SetCommitNames(new string[] { "BASE", "HEAD", @ref.GetName() });
							resolveMerger.SetWorkingTreeIterator(new FileTreeIterator(repo));
                            resolveMerger.SetMergeFilter(_mergeFilter);
							noProblems = merger.Merge(headCommit, srcCommit);
							lowLevelResults = resolveMerger.GetMergeResults();
							failingPaths = resolveMerger.GetFailingPaths();
							unmergedPaths = resolveMerger.GetUnmergedPaths();
						}
						else
						{
							noProblems = merger.Merge(headCommit, srcCommit);
						}
						refLogMessage.Append(": Merge made by ");
						refLogMessage.Append(mergeStrategy.GetName());
						refLogMessage.Append('.');
						if (noProblems)
						{
							dco = new DirCacheCheckout(repo, headCommit.Tree, repo.LockDirCache(), merger.GetResultTreeId
								());
							dco.SetFailOnConflict(true);
							dco.Checkout();
							string msg = null;
							RevCommit newHead = null;
							MergeStatus mergeStatus = null;
							if (!squash)
							{
								newHead = new Git(GetRepository()).Commit().SetReflogComment(refLogMessage.ToString
									()).Call();
								mergeStatus = MergeStatus.MERGED;
							}
							else
							{
								msg = JGitText.Get().squashCommitNotUpdatingHEAD;
								newHead = headCommit;
								mergeStatus = MergeStatus.MERGED_SQUASHED;
							}
							return new MergeCommandResult(newHead.Id, null, new ObjectId[] { headCommit.Id, srcCommit
								.Id }, mergeStatus, mergeStrategy, null, msg);
						}
						else
						{
							if (failingPaths != null)
							{
								repo.WriteMergeCommitMsg(null);
								repo.WriteMergeHeads(null);
								return new MergeCommandResult(null, merger.GetBaseCommit(0, 1), new ObjectId[] { 
									headCommit.Id, srcCommit.Id }, MergeStatus.FAILED, mergeStrategy, lowLevelResults
									, failingPaths, null);
							}
							else
							{
								string mergeMessageWithConflicts = new MergeMessageFormatter().FormatWithConflicts
									(mergeMessage, unmergedPaths);
								repo.WriteMergeCommitMsg(mergeMessageWithConflicts);
								return new MergeCommandResult(null, merger.GetBaseCommit(0, 1), new ObjectId[] { 
									headCommit.Id, srcCommit.Id }, MergeStatus.CONFLICTING, mergeStrategy, lowLevelResults
									, null);
							}
						}
					}
				}
			}
			catch (NGit.Errors.CheckoutConflictException e)
			{
				IList<string> conflicts = (dco == null) ? Sharpen.Collections.EmptyList<string>()
					 : dco.GetConflicts();
				throw new NGit.Api.Errors.CheckoutConflictException(conflicts, e);
			}
			catch (IOException e)
			{
				throw new JGitInternalException(MessageFormat.Format(JGitText.Get().exceptionCaughtDuringExecutionOfMergeCommand
					, e), e);
			}
			finally
			{
				if (revWalk != null)
				{
					revWalk.Release();
				}
			}
		}

		/// <exception cref="System.IO.IOException"></exception>
		/// <exception cref="NGit.Api.Errors.ConcurrentRefUpdateException"></exception>
		private void UpdateHead(StringBuilder refLogMessage, ObjectId newHeadId, ObjectId
			 oldHeadID)
		{
			RefUpdate refUpdate = repo.UpdateRef(Constants.HEAD);
			refUpdate.SetNewObjectId(newHeadId);
			refUpdate.SetRefLogMessage(refLogMessage.ToString(), false);
			refUpdate.SetExpectedOldObjectId(oldHeadID);
			RefUpdate.Result rc = refUpdate.Update();
			switch (rc)
			{
				case RefUpdate.Result.NEW:
				case RefUpdate.Result.FAST_FORWARD:
				{
					return;
				}

				case RefUpdate.Result.REJECTED:
				case RefUpdate.Result.LOCK_FAILURE:
				{
					throw new ConcurrentRefUpdateException(JGitText.Get().couldNotLockHEAD, refUpdate
						.GetRef(), rc);
				}

				default:
				{
					throw new JGitInternalException(MessageFormat.Format(JGitText.Get().updatingRefFailed
						, Constants.HEAD, newHeadId.ToString(), rc));
				}
			}
		}

		/// <param name="mergeStrategy">
		/// the
		/// <see cref="NGit.Merge.MergeStrategy">NGit.Merge.MergeStrategy</see>
		/// to be used
		/// </param>
		/// <returns>
		/// 
		/// <code>this</code>
		/// </returns>
		public virtual NGit.Api.MergeCommand SetStrategy(MergeStrategy mergeStrategy)
		{
			CheckCallable();
			this.mergeStrategy = mergeStrategy;
			return this;
		}

		/// <param name="commit">a reference to a commit which is merged with the current head
		/// 	</param>
		/// <returns>
		/// 
		/// <code>this</code>
		/// </returns>
		public virtual NGit.Api.MergeCommand Include(Ref commit)
		{
			CheckCallable();
			commits.AddItem(commit);
			return this;
		}

		/// <param name="commit">the Id of a commit which is merged with the current head</param>
		/// <returns>
		/// 
		/// <code>this</code>
		/// </returns>
		public virtual NGit.Api.MergeCommand Include(AnyObjectId commit)
		{
			return Include(commit.GetName(), commit);
		}

		/// <param name="name">a name given to the commit</param>
		/// <param name="commit">the Id of a commit which is merged with the current head</param>
		/// <returns>
		/// 
		/// <code>this</code>
		/// </returns>
		public virtual NGit.Api.MergeCommand Include(string name, AnyObjectId commit)
		{
			return Include(new ObjectIdRef.Unpeeled(RefStorage.LOOSE, name, commit.Copy()));
		}

		/// <summary>
		/// If <code>true</code>, will prepare the next commit in working tree and
		/// index as if a real merge happened, but do not make the commit or move the
		/// HEAD.
		/// </summary>
		/// <remarks>
		/// If <code>true</code>, will prepare the next commit in working tree and
		/// index as if a real merge happened, but do not make the commit or move the
		/// HEAD. Otherwise, perform the merge and commit the result.
		/// <p>
		/// In case the merge was successful but this flag was set to
		/// <code>true</code> a
		/// <see cref="MergeCommandResult">MergeCommandResult</see>
		/// with status
		/// <see cref="MergeStatus.MERGED_SQUASHED">MergeStatus.MERGED_SQUASHED</see>
		/// or
		/// <see cref="MergeStatus.FAST_FORWARD_SQUASHED">MergeStatus.FAST_FORWARD_SQUASHED</see>
		/// is returned.
		/// </remarks>
		/// <param name="squash">whether to squash commits or not</param>
		/// <returns>
		/// 
		/// <code>this</code>
		/// </returns>
		/// <since>2.0</since>
		public virtual NGit.Api.MergeCommand SetSquash(bool squash)
		{
			CheckCallable();
			this.squash = squash;
			return this;
		}

        /// <summary>
        /// Sets the merge filter for conflicting merges between Ours and Theirs.
        /// The filter is passed on to the <see cref="ResolveMerger"/>
        /// </summary>
        /// <remarks>
        /// The returned integer should be 1 for Ours or 2 for Theirs.
        /// </remarks>
        /// <param name="mergeFilter"></param>
        public virtual NGit.Api.MergeCommand SetMergeFilter(Func<string, int> mergeFilter)
        {
            this._mergeFilter = mergeFilter;
            return this;
        }
	}
}
