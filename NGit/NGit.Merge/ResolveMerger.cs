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
using System.Linq;
using NGit;
using NGit.Diff;
using NGit.Dircache;
using NGit.Errors;
using NGit.Internal;
using NGit.Merge;
using NGit.Treewalk;
using NGit.Util;
using Sharpen;

namespace NGit.Merge
{
	/// <summary>A three-way merger performing a content-merge if necessary</summary>
	public class ResolveMerger : ThreeWayMerger
	{
		/// <summary>
		/// If the merge fails (means: not stopped because of unresolved conflicts)
		/// this enum is used to explain why it failed
		/// </summary>
		public enum MergeFailureReason
		{
			DIRTY_INDEX,
			DIRTY_WORKTREE,
			COULD_NOT_DELETE
		}

        private NameConflictTreeWalk tw;

		private string[] commitNames;

		private const int T_BASE = 0;

		private const int T_OURS = 1;

		private const int T_THEIRS = 2;

		private const int T_INDEX = 3;

		private const int T_FILE = 4;

		private DirCacheBuilder builder;

		private ObjectId resultTree;

		private IList<string> unmergedPaths = new AList<string>();

		private IList<string> modifiedFiles = new List<string>();

		private IDictionary<string, DirCacheEntry> toBeCheckedOut = new Dictionary<string
			, DirCacheEntry>();

		private IList<string> toBeDeleted = new AList<string>();

		private IDictionary<string, MergeResult<Sequence>> mergeResults = new Dictionary<
			string, MergeResult<Sequence>>();

		private IDictionary<string, ResolveMerger.MergeFailureReason> failingPaths = new 
			Dictionary<string, ResolveMerger.MergeFailureReason>();

		private bool enterSubtree;

		private bool inCore;

		private DirCache dircache;

		private WorkingTreeIterator workingTreeIterator;

		private MergeAlgorithm mergeAlgorithm;

	    private Func<string, int> _mergeFilter;

		/// <param name="local"></param>
		/// <param name="inCore"></param>
		protected internal ResolveMerger(Repository local, bool inCore) : base(local)
		{
			DiffAlgorithm.SupportedAlgorithm diffAlg = local.GetConfig().GetEnum(ConfigConstants
				.CONFIG_DIFF_SECTION, null, ConfigConstants.CONFIG_KEY_ALGORITHM, DiffAlgorithm.SupportedAlgorithm
				.HISTOGRAM);
			mergeAlgorithm = new MergeAlgorithm(DiffAlgorithm.GetAlgorithm(diffAlg));
			commitNames = new string[] { "BASE", "OURS", "THEIRS" };
			this.inCore = inCore;
			if (inCore)
			{
				dircache = DirCache.NewInCore();
			}
		}

		/// <param name="local"></param>
		protected internal ResolveMerger(Repository local) : this(local, false)
		{
		}

		/// <exception cref="System.IO.IOException"></exception>
		protected internal override bool MergeImpl()
		{
			bool implicitDirCache = false;
			if (dircache == null)
			{
				dircache = GetRepository().LockDirCache();
				implicitDirCache = true;
			}
			try
			{
				builder = dircache.Builder();
				DirCacheBuildIterator buildIt = new DirCacheBuildIterator(builder);
				tw = new NameConflictTreeWalk(db);
				tw.AddTree(MergeBase());
				tw.AddTree(sourceTrees[0]);
				tw.AddTree(sourceTrees[1]);
				tw.AddTree(buildIt);
				if (workingTreeIterator != null)
				{
					tw.AddTree(workingTreeIterator);
				}
				while (tw.Next())
				{
					if (!ProcessEntry(tw.GetTree<CanonicalTreeParser>(T_BASE), tw.GetTree<CanonicalTreeParser
						>(T_OURS), tw.GetTree<CanonicalTreeParser>(T_THEIRS), tw.GetTree<DirCacheBuildIterator
						>(T_INDEX), (workingTreeIterator == null) ? null : tw.GetTree<WorkingTreeIterator
						>(T_FILE)))
					{
						CleanUp();
						return false;
					}
					if (tw.IsSubtree && enterSubtree)
					{
						tw.EnterSubtree();
					}
				}
				if (!inCore)
				{
					// No problem found. The only thing left to be done is to
					// checkout all files from "theirs" which have been selected to
					// go into the new index.
					Checkout();
					// All content-merges are successfully done. If we can now write the
					// new index we are on quite safe ground. Even if the checkout of
					// files coming from "theirs" fails the user can work around such
					// failures by checking out the index again.
					if (!builder.Commit())
					{
						CleanUp();
						throw new IndexWriteException();
					}
					builder = null;
				}
				else
				{
					builder.Finish();
					builder = null;
				}
				if (GetUnmergedPaths().IsEmpty() && !Failed())
				{
					resultTree = dircache.WriteTree(GetObjectInserter());
					return true;
				}
				else
				{
					resultTree = null;
					return false;
				}
			}
			finally
			{
				if (implicitDirCache)
				{
					dircache.Unlock();
				}
			}
		}

		/// <exception cref="NGit.Errors.NoWorkTreeException"></exception>
		/// <exception cref="System.IO.IOException"></exception>
		private void Checkout()
		{
			ObjectReader r = db.ObjectDatabase.NewReader();
			try
			{
				foreach (KeyValuePair<string, DirCacheEntry> entry in toBeCheckedOut.EntrySet())
				{
					FilePath f = new FilePath(db.WorkTree, entry.Key);
					CreateDir(f.GetParentFile());
					DirCacheCheckout.CheckoutEntry(db, f, entry.Value, r);
					modifiedFiles.AddItem(entry.Key);
				}
				// Iterate in reverse so that "folder/file" is deleted before
				// "folder". Otherwise this could result in a failing path because
				// of a non-empty directory, for which delete() would fail.
				for (int i = toBeDeleted.Count - 1; i >= 0; i--)
				{
					string fileName = toBeDeleted[i];
					FilePath f = new FilePath(db.WorkTree, fileName);
					if (!f.Delete())
					{
						failingPaths.Put(fileName, ResolveMerger.MergeFailureReason.COULD_NOT_DELETE);
					}
					modifiedFiles.AddItem(fileName);
				}
			}
			finally
			{
				r.Release();
			}
		}

		/// <exception cref="System.IO.IOException"></exception>
		private void CreateDir(FilePath f)
		{
			if (!f.IsDirectory() && !f.Mkdirs())
			{
				FilePath p = f;
				while (p != null && !p.Exists())
				{
					p = p.GetParentFile();
				}
				if (p == null || p.IsDirectory())
				{
					throw new IOException(JGitText.Get().cannotCreateDirectory);
				}
				FileUtils.Delete(p);
				if (!f.Mkdirs())
				{
					throw new IOException(JGitText.Get().cannotCreateDirectory);
				}
			}
		}

		/// <summary>Reverts the worktree after an unsuccessful merge.</summary>
		/// <remarks>
		/// Reverts the worktree after an unsuccessful merge. We know that for all
		/// modified files the old content was in the old index and the index
		/// contained only stage 0. In case if inCore operation just clear
		/// the history of modified files.
		/// </remarks>
		/// <exception cref="System.IO.IOException">System.IO.IOException</exception>
		/// <exception cref="NGit.Errors.CorruptObjectException">NGit.Errors.CorruptObjectException
		/// 	</exception>
		/// <exception cref="NGit.Errors.NoWorkTreeException">NGit.Errors.NoWorkTreeException
		/// 	</exception>
		private void CleanUp()
		{
			if (inCore)
			{
				modifiedFiles.Clear();
				return;
			}
			DirCache dc = db.ReadDirCache();
			ObjectReader or = db.ObjectDatabase.NewReader();
			Iterator<string> mpathsIt = modifiedFiles.Iterator();
			while (mpathsIt.HasNext())
			{
				string mpath = mpathsIt.Next();
				DirCacheEntry entry = dc.GetEntry(mpath);
				FileOutputStream fos = new FileOutputStream(new FilePath(db.WorkTree, mpath));
				try
				{
					or.Open(entry.GetObjectId()).CopyTo(fos);
				}
				finally
				{
					fos.Close();
				}
				mpathsIt.Remove();
			}
		}

		/// <summary>adds a new path with the specified stage to the index builder</summary>
		/// <param name="path"></param>
		/// <param name="p"></param>
		/// <param name="stage"></param>
		/// <param name="lastMod"></param>
		/// <param name="len"></param>
		/// <returns>the entry which was added to the index</returns>
		private DirCacheEntry Add(byte[] path, CanonicalTreeParser p, int stage, long lastMod
			, long len)
		{
			if (p != null && !p.EntryFileMode.Equals(FileMode.TREE))
			{
				DirCacheEntry e = new DirCacheEntry(path, stage);
				e.FileMode = p.EntryFileMode;
				e.SetObjectId(p.EntryObjectId);
				e.LastModified = lastMod;
				e.SetLength(len);
				builder.Add(e);
				return e;
			}
			return null;
		}

		/// <summary>
		/// adds a entry to the index builder which is a copy of the specified
		/// DirCacheEntry
		/// </summary>
		/// <param name="e">the entry which should be copied</param>
		/// <returns>the entry which was added to the index</returns>
		private DirCacheEntry Keep(DirCacheEntry e)
		{
			DirCacheEntry newEntry = new DirCacheEntry(e.PathString, e.Stage);
			newEntry.FileMode = e.FileMode;
			newEntry.SetObjectId(e.GetObjectId());
			newEntry.LastModified = e.LastModified;
			newEntry.SetLength(e.Length);
			builder.Add(newEntry);
			return newEntry;
		}

		/// <summary>Processes one path and tries to merge.</summary>
		/// <remarks>
		/// Processes one path and tries to merge. This method will do all do all
		/// trivial (not content) merges and will also detect if a merge will fail.
		/// The merge will fail when one of the following is true
		/// <ul>
		/// <li>the index entry does not match the entry in ours. When merging one
		/// branch into the current HEAD, ours will point to HEAD and theirs will
		/// point to the other branch. It is assumed that the index matches the HEAD
		/// because it will only not match HEAD if it was populated before the merge
		/// operation. But the merge commit should not accidentally contain
		/// modifications done before the merge. Check the &lt;a href=
		/// "http://www.kernel.org/pub/software/scm/git/docs/git-read-tree.html#_3_way_merge"
		/// &gt;git read-tree</a> documentation for further explanations.</li>
		/// <li>A conflict was detected and the working-tree file is dirty. When a
		/// conflict is detected the content-merge algorithm will try to write a
		/// merged version into the working-tree. If the file is dirty we would
		/// override unsaved data.</li>
		/// </remarks>
		/// <param name="base">the common base for ours and theirs</param>
		/// <param name="ours">
		/// the ours side of the merge. When merging a branch into the
		/// HEAD ours will point to HEAD
		/// </param>
		/// <param name="theirs">
		/// the theirs side of the merge. When merging a branch into the
		/// current HEAD theirs will point to the branch which is merged
		/// into HEAD.
		/// </param>
		/// <param name="index">the index entry</param>
		/// <param name="work">the file in the working tree</param>
		/// <returns>
		/// <code>false</code> if the merge will fail because the index entry
		/// didn't match ours or the working-dir file was dirty and a
		/// conflict occurred
		/// </returns>
		/// <exception cref="NGit.Errors.MissingObjectException">NGit.Errors.MissingObjectException
		/// 	</exception>
		/// <exception cref="NGit.Errors.IncorrectObjectTypeException">NGit.Errors.IncorrectObjectTypeException
		/// 	</exception>
		/// <exception cref="NGit.Errors.CorruptObjectException">NGit.Errors.CorruptObjectException
		/// 	</exception>
		/// <exception cref="System.IO.IOException">System.IO.IOException</exception>
		private bool ProcessEntry(CanonicalTreeParser @base, CanonicalTreeParser ours, CanonicalTreeParser
			 theirs, DirCacheBuildIterator index, WorkingTreeIterator work)
		{
			enterSubtree = true;
			int modeO = tw.GetRawMode(T_OURS);
			int modeT = tw.GetRawMode(T_THEIRS);
			int modeB = tw.GetRawMode(T_BASE);
			if (modeO == 0 && modeT == 0 && modeB == 0)
			{
				// File is either untracked or new, staged but uncommitted
				return true;
			}
			if (IsIndexDirty())
			{
				return false;
			}
			DirCacheEntry ourDce = null;
			if (index == null || index.GetDirCacheEntry() == null)
			{
				// create a fake DCE, but only if ours is valid. ours is kept only
				// in case it is valid, so a null ourDce is ok in all other cases.
				if (NonTree(modeO))
				{
					ourDce = new DirCacheEntry(tw.RawPath);
					ourDce.SetObjectId(tw.GetObjectId(T_OURS));
					ourDce.FileMode = tw.GetFileMode(T_OURS);
				}
			}
			else
			{
				ourDce = index.GetDirCacheEntry();
			}
			if (NonTree(modeO) && NonTree(modeT) && tw.IdEqual(T_OURS, T_THEIRS))
			{
				// OURS and THEIRS have equal content. Check the file mode
				if (modeO == modeT)
				{
					// content and mode of OURS and THEIRS are equal: it doesn't
					// matter which one we choose. OURS is chosen. Since the index
					// is clean (the index matches already OURS) we can keep the existing one
					Keep(ourDce);
					// no checkout needed!
					return true;
				}
				else
				{
					// same content but different mode on OURS and THEIRS.
					// Try to merge the mode and report an error if this is
					// not possible.
					int newMode = MergeFileModes(modeB, modeO, modeT);
					if (newMode != FileMode.MISSING.GetBits())
					{
						if (newMode == modeO)
						{
							// ours version is preferred
							Keep(ourDce);
						}
						else
						{
							// the preferred version THEIRS has a different mode
							// than ours. Check it out!
							if (IsWorktreeDirty(work, false))
							{
                                if (_mergeFilter != null)
                                {
                                    var whos = _mergeFilter(tw.PathString);
                                    if (whos == T_THEIRS)
                                    {
                                        //Choose THEIRS
                                        DirCacheEntry eT = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
                                        toBeCheckedOut.Put(tw.PathString, eT);
                                        return true;
                                    }
                                    //Choose OURS
                                    Keep(ourDce);
                                    return true;
                                }

							    IsWorktreeDirty(work);
								return false;
							}
							// we know about length and lastMod only after we have written the new content.
							// This will happen later. Set these values to 0 for know.
							DirCacheEntry e = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
							toBeCheckedOut.Put(tw.PathString, e);
						}
						return true;
					}
					else
					{
					    if (_mergeFilter != null)
					    {
					        var whos = _mergeFilter(tw.PathString);
					        if (whos == T_THEIRS)
					        {
                                //Choose THEIRS
                                DirCacheEntry e = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
                                toBeCheckedOut.Put(tw.PathString, e);
                                return true;
					        }
                            //Choose OURS
                            Keep(ourDce);
					        return true;
					    }

						// FileModes are not mergeable. We found a conflict on modes.
						// For conflicting entries we don't know lastModified and length.
                        Add(tw.RawPath, @base, DirCacheEntry.STAGE_1, 0, 0);
                        Add(tw.RawPath, ours, DirCacheEntry.STAGE_2, 0, 0);
                        Add(tw.RawPath, theirs, DirCacheEntry.STAGE_3, 0, 0);
                        unmergedPaths.AddItem(tw.PathString);
                        mergeResults.Put(tw.PathString, new MergeResult<RawText>(Sharpen.Collections.EmptyList
                            <RawText>()).Upcast());
					}
					return true;
				}
			}
			if (NonTree(modeO) && modeB == modeT && tw.IdEqual(T_BASE, T_THEIRS))
			{
				// THEIRS was not changed compared to BASE. All changes must be in
				// OURS. OURS is chosen. We can keep the existing entry.
				Keep(ourDce);
				// no checkout needed!
				return true;
			}
			if (modeB == modeO && tw.IdEqual(T_BASE, T_OURS))
			{
				// OURS was not changed compared to BASE. All changes must be in
				// THEIRS. THEIRS is chosen.
				// Check worktree before checking out THEIRS
				if (IsWorktreeDirty(work, false))
				{
                    if (_mergeFilter != null)
                    {
                        var whos = _mergeFilter(tw.PathString);
                        if (whos == T_THEIRS)
                        {
                            //Choose THEIRS
                            DirCacheEntry eT = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
                            toBeCheckedOut.Put(tw.PathString, eT);
                            return true;
                        }
                        //Choose OURS
                        Keep(ourDce);
                        return true;
                    }

				    IsWorktreeDirty(work);
					return false;
				}
				if (NonTree(modeT))
				{
					// we know about length and lastMod only after we have written
					// the new content.
					// This will happen later. Set these values to 0 for know.
					DirCacheEntry e = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
					if (e != null)
					{
						toBeCheckedOut.Put(tw.PathString, e);
					}
					return true;
				}
				else
				{
					if (modeT == 0 && modeB != 0)
					{
						// we want THEIRS ... but THEIRS contains the deletion of the
						// file
						toBeDeleted.AddItem(tw.PathString);
						return true;
					}
				}
			}
			if (tw.IsSubtree)
			{
				// file/folder conflicts: here I want to detect only file/folder
				// conflict between ours and theirs. file/folder conflicts between
				// base/index/workingTree and something else are not relevant or
				// detected later
				if (NonTree(modeO) && !NonTree(modeT))
				{
					if (NonTree(modeB))
					{
						Add(tw.RawPath, @base, DirCacheEntry.STAGE_1, 0, 0);
					}
					Add(tw.RawPath, ours, DirCacheEntry.STAGE_2, 0, 0);
					unmergedPaths.AddItem(tw.PathString);
					enterSubtree = false;
					return true;
				}
				if (NonTree(modeT) && !NonTree(modeO))
				{
					if (NonTree(modeB))
					{
						Add(tw.RawPath, @base, DirCacheEntry.STAGE_1, 0, 0);
					}
					Add(tw.RawPath, theirs, DirCacheEntry.STAGE_3, 0, 0);
					unmergedPaths.AddItem(tw.PathString);
					enterSubtree = false;
					return true;
				}
				// ours and theirs are both folders or both files (and treewalk
				// tells us we are in a subtree because of index or working-dir).
				// If they are both folders no content-merge is required - we can
				// return here.
				if (!NonTree(modeO))
				{
					return true;
				}
			}
			// ours and theirs are both files, just fall out of the if block
			// and do the content merge
			if (NonTree(modeO) && NonTree(modeT))
			{
				// Check worktree before modifying files
				if (IsWorktreeDirty(work, false))
				{
                    if (_mergeFilter != null)
                    {
                        var whos = _mergeFilter(tw.PathString);
                        if (whos == T_THEIRS)
                        {
                            //Choose THEIRS
                            DirCacheEntry eT = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
                            toBeCheckedOut.Put(tw.PathString, eT);
                            return true;
                        }
                        //Choose OURS
                        Keep(ourDce);
                        return true;
                    }

				    IsWorktreeDirty(work);
					return false;
				}
				// Don't attempt to resolve submodule link conflicts
				if (IsGitLink(modeO) || IsGitLink(modeT))
				{
					Add(tw.RawPath, @base, DirCacheEntry.STAGE_1, 0, 0);
					Add(tw.RawPath, ours, DirCacheEntry.STAGE_2, 0, 0);
					Add(tw.RawPath, theirs, DirCacheEntry.STAGE_3, 0, 0);
					unmergedPaths.AddItem(tw.PathString);
					return true;
				}
                //Do the content merge
				MergeResult<RawText> result = ContentMerge(@base, ours, theirs);
                //If conflicts exists and a merge filter is available we choose based on the outcome
                if(result.ContainsConflicts() && _mergeFilter != null)
                {
                    var whos = _mergeFilter(tw.PathString);
                    if (whos == T_THEIRS)
                    {
                        //Choose THEIRS
                        DirCacheEntry e = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
                        toBeCheckedOut.Put(tw.PathString, e);
                        return true;
                    }

                    //Choose OURS
                    Keep(ourDce);
                    return true;
                }
                //Default to merge conflict
				FilePath of = WriteMergedFile(result);
				UpdateIndex(@base, ours, theirs, result, of);
				if (result.ContainsConflicts())
				{
					unmergedPaths.AddItem(tw.PathString);
				}
				modifiedFiles.AddItem(tw.PathString);
			}
			else
			{
				if (modeO != modeT)
				{
					// OURS or THEIRS has been deleted
					if (((modeO != 0 && !tw.IdEqual(T_BASE, T_OURS)) || (modeT != 0 && !tw.IdEqual(T_BASE, T_THEIRS))))
					{
						Add(tw.RawPath, @base, DirCacheEntry.STAGE_1, 0, 0);
						Add(tw.RawPath, ours, DirCacheEntry.STAGE_2, 0, 0);
						DirCacheEntry e = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_3, 0, 0);
						// OURS was deleted checkout THEIRS
						if (modeO == 0)
						{
							// Check worktree before checking out THEIRS
							if (IsWorktreeDirty(work, false))
							{
                                if (_mergeFilter != null)
                                {
                                    var whos = _mergeFilter(tw.PathString);
                                    if (whos == T_THEIRS)
                                    {
                                        //Choose THEIRS
                                        DirCacheEntry eT = Add(tw.RawPath, theirs, DirCacheEntry.STAGE_0, 0, 0);
                                        toBeCheckedOut.Put(tw.PathString, eT);
                                        return true;
                                    }
                                    //Choose OURS
                                    Keep(ourDce);
                                    return true;
                                }

							    IsWorktreeDirty(work);
								return false;
							}
							if (NonTree(modeT))
							{
								if (e != null)
								{
									toBeCheckedOut.Put(tw.PathString, e);
								}
							}
						}
						unmergedPaths.AddItem(tw.PathString);
						// generate a MergeResult for the deleted file
						mergeResults.Put(tw.PathString, ContentMerge(@base, ours, theirs).Upcast ());
					}
				}
			}
			return true;
		}

		/// <summary>Does the content merge.</summary>
		/// <remarks>
		/// Does the content merge. The three texts base, ours and theirs are
		/// specified with
		/// <see cref="NGit.Treewalk.CanonicalTreeParser">NGit.Treewalk.CanonicalTreeParser</see>
		/// . If any of the parsers is
		/// specified as <code>null</code> then an empty text will be used instead.
		/// </remarks>
		/// <param name="base"></param>
		/// <param name="ours"></param>
		/// <param name="theirs"></param>
		/// <returns>the result of the content merge</returns>
		/// <exception cref="System.IO.IOException">System.IO.IOException</exception>
		private MergeResult<RawText> ContentMerge(CanonicalTreeParser @base, CanonicalTreeParser
			 ours, CanonicalTreeParser theirs)
		{
			RawText baseText = @base == null ? RawText.EMPTY_TEXT : GetRawText(@base.EntryObjectId
				, db);
			RawText ourText = ours == null ? RawText.EMPTY_TEXT : GetRawText(ours.EntryObjectId
				, db);
			RawText theirsText = theirs == null ? RawText.EMPTY_TEXT : GetRawText(theirs.EntryObjectId
				, db);
			return (mergeAlgorithm.Merge(RawTextComparator.DEFAULT, baseText, ourText, theirsText
				));
		}

        /// <summary>
        /// Checks whether the index is dirty
        /// </summary>
        /// <param name="commitWhenDirty">Boolean indicating whether to add entry to failing paths</param>
        /// <returns></returns>
        private bool IsIndexDirty(bool commitWhenDirty = true)
		{
			int modeI = tw.GetRawMode(T_INDEX);
			int modeO = tw.GetRawMode(T_OURS);
			// Index entry has to match ours to be considered clean
			bool isDirty = NonTree(modeI) && !(modeO == modeI && tw.IdEqual(T_INDEX, T_OURS));
            if (isDirty && commitWhenDirty)
			{
				failingPaths.Put(tw.PathString, ResolveMerger.MergeFailureReason.DIRTY_INDEX);
			}
			return isDirty;
		}

        /// <summary>
        /// Checks whether the worktree is dirty
        /// </summary>
        /// <remarks>The CommitWhenDirty parameter gives us a chance to check and react before adding the entry as a failed path</remarks>
        /// <param name="work"></param>
        /// <param name="commitWhenDirty">Boolean indicating whether to add entry to failing paths</param>
        /// <returns></returns>
		private bool IsWorktreeDirty(WorkingTreeIterator work, bool commitWhenDirty = true)
		{
			if (inCore || work == null)
			{
				return false;
			}
			int modeF = tw.GetRawMode(T_FILE);
			int modeO = tw.GetRawMode(T_OURS);
			// Worktree entry has to match ours to be considered clean
			bool isDirty = work.IsModeDifferent(modeO);
			if (!isDirty && NonTree(modeF))
			{
				isDirty = !tw.IdEqual(T_FILE, T_OURS);
			}
			if (isDirty && commitWhenDirty)
			{
				failingPaths.Put(tw.PathString, ResolveMerger.MergeFailureReason.DIRTY_WORKTREE);
			}
			return isDirty;
		}

		/// <summary>Updates the index after a content merge has happened.</summary>
		/// <remarks>
		/// Updates the index after a content merge has happened. If no conflict has
		/// occurred this includes persisting the merged content to the object
		/// database. In case of conflicts this method takes care to write the
		/// correct stages to the index.
		/// </remarks>
		/// <param name="base"></param>
		/// <param name="ours"></param>
		/// <param name="theirs"></param>
		/// <param name="result"></param>
		/// <param name="of"></param>
		/// <exception cref="System.IO.FileNotFoundException">System.IO.FileNotFoundException
		/// 	</exception>
		/// <exception cref="System.IO.IOException">System.IO.IOException</exception>
		private void UpdateIndex(CanonicalTreeParser @base, CanonicalTreeParser ours, CanonicalTreeParser
			 theirs, MergeResult<RawText> result, FilePath of)
		{
			if (result.ContainsConflicts())
			{
				// a conflict occurred, the file will contain conflict markers
				// the index will be populated with the three stages and only the
				// workdir (if used) contains the halfways merged content
				Add(tw.RawPath, @base, DirCacheEntry.STAGE_1, 0, 0);
				Add(tw.RawPath, ours, DirCacheEntry.STAGE_2, 0, 0);
				Add(tw.RawPath, theirs, DirCacheEntry.STAGE_3, 0, 0);
				mergeResults.Put(tw.PathString, result.Upcast ());
			}
			else
			{
				// no conflict occurred, the file will contain fully merged content.
				// the index will be populated with the new merged version
				DirCacheEntry dce = new DirCacheEntry(tw.PathString);
				int newMode = MergeFileModes(tw.GetRawMode(0), tw.GetRawMode(1), tw.GetRawMode(2)
					);
				// set the mode for the new content. Fall back to REGULAR_FILE if
				// you can't merge modes of OURS and THEIRS
				dce.FileMode = (newMode == FileMode.MISSING.GetBits()) ? FileMode.REGULAR_FILE : 
					FileMode.FromBits(newMode);
				dce.LastModified = of.LastModified();
				dce.SetLength((int)of.Length());
				InputStream @is = new FileInputStream(of);
				try
				{
					dce.SetObjectId(GetObjectInserter().Insert(Constants.OBJ_BLOB, of.Length(), @is));
				}
				finally
				{
					@is.Close();
					if (inCore)
					{
						FileUtils.Delete(of);
					}
				}
				builder.Add(dce);
			}
		}

		/// <summary>Writes merged file content to the working tree.</summary>
		/// <remarks>
		/// Writes merged file content to the working tree. In case
		/// <see cref="inCore">inCore</see>
		/// is set and we don't have a working tree the content is written to a
		/// temporary file
		/// </remarks>
		/// <param name="result">the result of the content merge</param>
		/// <returns>the file to which the merged content was written</returns>
		/// <exception cref="System.IO.FileNotFoundException">System.IO.FileNotFoundException
		/// 	</exception>
		/// <exception cref="System.IO.IOException">System.IO.IOException</exception>
		private FilePath WriteMergedFile(MergeResult<RawText> result)
		{
			MergeFormatter fmt = new MergeFormatter();
			FilePath of = null;
			FileOutputStream fos;
			if (!inCore)
			{
				FilePath workTree = db.WorkTree;
				if (workTree == null)
				{
					// TODO: This should be handled by WorkingTreeIterators which
					// support write operations
					throw new NGit.Errors.NotSupportedException();
				}
				of = new FilePath(workTree, tw.PathString);
				FilePath parentFolder = of.GetParentFile();
				if (!parentFolder.Exists())
				{
					parentFolder.Mkdirs();
				}
				fos = new FileOutputStream(of);
				try
				{
					fmt.FormatMerge(fos, result, Arrays.AsList(commitNames), Constants.CHARACTER_ENCODING
						);
				}
				finally
				{
					fos.Close();
				}
			}
			else
			{
				if (!result.ContainsConflicts())
				{
					// When working inCore, only trivial merges can be handled,
					// so we generate objects only in conflict free cases
					of = FilePath.CreateTempFile("merge_", "_temp", null);
					fos = new FileOutputStream(of);
					try
					{
						fmt.FormatMerge(fos, result, Arrays.AsList(commitNames), Constants.CHARACTER_ENCODING
							);
					}
					finally
					{
						fos.Close();
					}
				}
			}
			return of;
		}

		/// <summary>Try to merge filemodes.</summary>
		/// <remarks>
		/// Try to merge filemodes. If only ours or theirs have changed the mode
		/// (compared to base) we choose that one. If ours and theirs have equal
		/// modes return that one. If also that is not the case the modes are not
		/// mergeable. Return
		/// <see cref="NGit.FileMode.MISSING">NGit.FileMode.MISSING</see>
		/// int that case.
		/// </remarks>
		/// <param name="modeB">filemode found in BASE</param>
		/// <param name="modeO">filemode found in OURS</param>
		/// <param name="modeT">filemode found in THEIRS</param>
		/// <returns>
		/// the merged filemode or
		/// <see cref="NGit.FileMode.MISSING">NGit.FileMode.MISSING</see>
		/// in case of a
		/// conflict
		/// </returns>
		private int MergeFileModes(int modeB, int modeO, int modeT)
		{
			if (modeO == modeT)
			{
				return modeO;
			}
			if (modeB == modeO)
			{
				// Base equal to Ours -> chooses Theirs if that is not missing
				return (modeT == FileMode.MISSING.GetBits()) ? modeO : modeT;
			}
			if (modeB == modeT)
			{
				// Base equal to Theirs -> chooses Ours if that is not missing
				return (modeO == FileMode.MISSING.GetBits()) ? modeT : modeO;
			}
			return FileMode.MISSING.GetBits();
		}

		/// <exception cref="System.IO.IOException"></exception>
		private static RawText GetRawText(ObjectId id, Repository db)
		{
			if (id.Equals(ObjectId.ZeroId))
			{
				return new RawText(new byte[] {  });
			}
			return new RawText(db.Open(id, Constants.OBJ_BLOB).GetCachedBytes());
		}

		private static bool NonTree(int mode)
		{
			return mode != 0 && !FileMode.TREE.Equals(mode);
		}

		private static bool IsGitLink(int mode)
		{
			return FileMode.GITLINK.Equals(mode);
		}

		public override ObjectId GetResultTreeId()
		{
			return (resultTree == null) ? null : resultTree.ToObjectId();
		}

		/// <param name="commitNames">
		/// the names of the commits as they would appear in conflict
		/// markers
		/// </param>
		public virtual void SetCommitNames(string[] commitNames)
		{
			this.commitNames = commitNames;
		}

		/// <returns>
		/// the names of the commits as they would appear in conflict
		/// markers.
		/// </returns>
		public virtual string[] GetCommitNames()
		{
			return commitNames;
		}

		/// <returns>
		/// the paths with conflicts. This is a subset of the files listed
		/// by
		/// <see cref="GetModifiedFiles()">GetModifiedFiles()</see>
		/// </returns>
		public virtual IList<string> GetUnmergedPaths()
		{
			return unmergedPaths;
		}

		/// <returns>
		/// the paths of files which have been modified by this merge. A
		/// file will be modified if a content-merge works on this path or if
		/// the merge algorithm decides to take the theirs-version. This is a
		/// superset of the files listed by
		/// <see cref="GetUnmergedPaths()">GetUnmergedPaths()</see>
		/// .
		/// </returns>
		public virtual IList<string> GetModifiedFiles()
		{
			return modifiedFiles;
		}

		/// <returns>
		/// a map which maps the paths of files which have to be checked out
		/// because the merge created new fully-merged content for this file
		/// into the index. This means: the merge wrote a new stage 0 entry
		/// for this path.
		/// </returns>
		public virtual IDictionary<string, DirCacheEntry> GetToBeCheckedOut()
		{
			return toBeCheckedOut;
		}

		/// <returns>the mergeResults</returns>
		public virtual IDictionary<string, MergeResult<Sequence>> GetMergeResults()
		{
			return mergeResults;
		}

		/// <returns>
		/// lists paths causing this merge to fail (not stopped because of a
		/// conflict). <code>null</code> is returned if this merge didn't
		/// fail.
		/// </returns>
		public virtual IDictionary<string, ResolveMerger.MergeFailureReason> GetFailingPaths()
		{
			return (failingPaths.Count == 0) ? null : failingPaths;
		}

		/// <summary>Returns whether this merge failed (i.e.</summary>
		/// <remarks>
		/// Returns whether this merge failed (i.e. not stopped because of a
		/// conflict)
		/// </remarks>
		/// <returns>
		/// <code>true</code> if a failure occurred, <code>false</code>
		/// otherwise
		/// </returns>
		public virtual bool Failed()
		{
			return failingPaths.Count > 0;
		}

		/// <summary>Sets the DirCache which shall be used by this merger.</summary>
		/// <remarks>
		/// Sets the DirCache which shall be used by this merger. If the DirCache is
		/// not set explicitly this merger will implicitly get and lock a default
		/// DirCache. If the DirCache is explicitly set the caller is responsible to
		/// lock it in advance. Finally the merger will call
		/// <see cref="NGit.Dircache.DirCache.Commit()">NGit.Dircache.DirCache.Commit()</see>
		/// which requires that the DirCache is locked. If
		/// the
		/// <see cref="MergeImpl()">MergeImpl()</see>
		/// returns without throwing an exception the lock
		/// will be released. In case of exceptions the caller is responsible to
		/// release the lock.
		/// </remarks>
		/// <param name="dc">the DirCache to set</param>
		public virtual void SetDirCache(DirCache dc)
		{
			this.dircache = dc;
		}

		/// <summary>Sets the WorkingTreeIterator to be used by this merger.</summary>
		/// <remarks>
		/// Sets the WorkingTreeIterator to be used by this merger. If no
		/// WorkingTreeIterator is set this merger will ignore the working tree and
		/// fail if a content merge is necessary.
		/// <p>
		/// TODO: enhance WorkingTreeIterator to support write operations. Then this
		/// merger will be able to merge with a different working tree abstraction.
		/// </remarks>
		/// <param name="workingTreeIterator">the workingTreeIt to set</param>
		public virtual void SetWorkingTreeIterator(WorkingTreeIterator workingTreeIterator)
		{
			this.workingTreeIterator = workingTreeIterator;
		}

        /// <summary>
        /// Sets the merge filter for conflicting merges between Ours and Theirs
        /// </summary>
        /// <remarks>
        /// The returned integer should be 1 for Ours or 2 for Theirs.
        /// </remarks>
        /// <param name="mergeFilter"></param>
	    public virtual void SetMergeFilter(Func<string, int> mergeFilter)
	    {
	        this._mergeFilter = mergeFilter;
	    }
	}
}
