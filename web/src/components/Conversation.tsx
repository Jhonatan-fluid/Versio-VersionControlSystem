import { useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import type { Reaction, ReviewThread, TimelineItem } from '../api';
import { FragmentHost } from './FragmentHost';
import { Avatar, Icon, IssueLabel, RelativeTime, type IconName } from './ui';

/**
 * The conversation's pieces, as github.com draws them (docs/web/FINISH.md, B3): a comment is a
 * card with the person's picture beside it and a grey header naming them, their role and when;
 * everything else is a row on the rail with an icon badge and one sentence; a review is a card
 * of its own with its threads listed under it. Nothing here writes: the boxes that do come from
 * the page as children, so the operation-key rules in ReviewWrite stay in one place.
 */

/** GitHub's role badge beside an author: who they are to the repository, when it says anything. */
export function RoleBadge({ association }: { association?: string | null }) {
  const words: Record<string, string> = { OWNER: 'Owner', MEMBER: 'Member', COLLABORATOR: 'Collaborator', CONTRIBUTOR: 'Contributor', FIRST_TIME_CONTRIBUTOR: 'First-time contributor', FIRST_TIMER: 'First-timer' };
  const word = association ? words[association] : undefined;
  return word ? <span className="role-badge">{word}</span> : null;
}

/** The emoji GitHub draws for each reaction word. */
const emoji: Record<string, string> = {
  THUMBS_UP: '👍', THUMBS_DOWN: '👎', LAUGH: '😄', HOORAY: '🎉', CONFUSED: '😕', HEART: '❤️', ROCKET: '🚀', EYES: '👀',
};

/** GitHub's eight reactions, in the order its picker shows them. */
export const reactionWords = ['THUMBS_UP', 'THUMBS_DOWN', 'LAUGH', 'HOORAY', 'CONFUSED', 'HEART', 'ROCKET', 'EYES'] as const;

/**
 * The reactions on a body, a comment or a review: pills, and — when the viewer may react — each
 * pill a toggle of their own reaction, with GitHub's picker to add another (FINISH.md Step 5).
 * `onToggle` is the page's: it posts, and nothing here keeps a key.
 */
export function Reactions({ reactions, onToggle }: { reactions?: Reaction[] | null; onToggle?: (content: string, on: boolean) => void }) {
  const [picking, setPicking] = useState(false);
  const list = reactions ?? [];
  if (list.length === 0 && !onToggle) {
    return null;
  }
  const pill = (r: Reaction) => (
    <span aria-hidden="true">{emoji[r.content] ?? r.content}</span>
  );
  return (
    <div className="reactions" aria-label="Reactions">
      {list.map((r) => onToggle ? (
        <button key={r.content} type="button" className={`reaction${r.viewerHasReacted ? ' is-mine' : ''}`} title={`${r.count} ${r.content.toLowerCase().replace('_', ' ')}`}
          aria-pressed={r.viewerHasReacted} onClick={() => onToggle(r.content, !r.viewerHasReacted)}>
          {pill(r)} {r.count}
        </button>
      ) : (
        <span key={r.content} className={`reaction${r.viewerHasReacted ? ' is-mine' : ''}`} title={`${r.count} ${r.content.toLowerCase().replace('_', ' ')}`}>
          {pill(r)} {r.count}
        </span>
      ))}
      {onToggle && (
        <span className="reaction-add">
          <button type="button" className="reaction reaction-add-button" aria-label="Add reaction" aria-expanded={picking} onClick={() => setPicking(!picking)}>
            <Icon name="smiley" size={14} />
          </button>
          {picking && (
            <span className="reaction-picker" role="menu">
              {reactionWords.map((w) => (
                <button key={w} type="button" role="menuitem" className={`reaction-pick${list.some((r) => r.content === w && r.viewerHasReacted) ? ' is-mine' : ''}`}
                  title={w.toLowerCase().replace('_', ' ')} onClick={() => { setPicking(false); onToggle(w, !list.some((r) => r.content === w && r.viewerHasReacted)); }}>
                  {emoji[w]}
                </button>
              ))}
            </span>
          )}
        </span>
      )}
    </div>
  );
}

/** GitHub's words for a hidden comment's reason. */
export function minimizedWords(reason?: string | null): string {
  const words: Record<string, string> = { SPAM: 'spam', ABUSE: 'abuse', OFF_TOPIC: 'off-topic', OUTDATED: 'outdated', DUPLICATE: 'duplicate', RESOLVED: 'resolved' };
  return reason ? words[reason] ?? reason.toLowerCase().replace('_', ' ') : 'hidden';
}

/**
 * A hidden comment, as github.com draws it: one line saying why, and the comment behind a "Show"
 * — folded, never dropped, so a hidden remark can still be read and unhidden.
 */
export function Minimized({ reason, children }: { reason?: string | null; children: ReactNode }) {
  const [shown, setShown] = useState(false);
  return (
    <div className="comment-minimized">
      <p className="comment-minimized-note text-muted">
        This comment was marked as {minimizedWords(reason)}.
        <button type="button" className="link-button" onClick={() => setShown(!shown)} aria-expanded={shown}>{shown ? 'Hide comment' : 'Show comment'}</button>
      </p>
      {shown && children}
    </div>
  );
}

interface CardProps {
  author: string;
  avatar?: string | null;
  association?: string | null;
  when: string;
  editedAt?: string | null;
  url?: string | null;
  /** The element id for a permalink (#issuecomment-…), from the comment's own GitHub URL. */
  anchor?: string | null;
  /** The header's verb: "commented", "approved these changes", "left a review". */
  verb?: ReactNode;
  /** Header tools: Edit, Delete, a resolve button. */
  tools?: ReactNode;
  /** A badge after the author: pending, resolved. */
  badge?: ReactNode;
  /** The card's colour: a review's state. */
  tone?: 'default' | 'success' | 'danger';
  reactions?: Reaction[] | null;
  /** The viewer's own reaction toggled: given only when they may react. */
  onReact?: (content: string, on: boolean) => void;
  /** Hidden on GitHub: the body folds behind its reason. */
  minimized?: boolean;
  minimizedReason?: string | null;
  children: ReactNode;
}

/** The permalink fragment GitHub uses, from a comment's URL ("#issuecomment-123"), so the page can target it. */
export function anchorOf(url?: string | null): string | null {
  const hash = url ? url.slice(url.indexOf('#') + 1) : '';
  // GitHub's three shapes: issuecomment-9 and pullrequestreview-1 with a hyphen, discussion_r7
  // with an underscore — which the first version did not accept, so no review-thread permalink
  // ever matched (review, 2026-10-24; found by the first test that used one).
  return url && url.includes('#') && /^[a-z]+[-_][a-z0-9]+$/i.test(hash) ? hash : null;
}

export function CommentCard({ author, avatar, association, when, editedAt, url, anchor, verb = 'commented', tools, badge, tone = 'default', reactions, onReact, minimized, minimizedReason, children }: CardProps) {
  const body = (
    <>
      <div className="comment-body">{children}</div>
      <Reactions reactions={reactions} onToggle={onReact} />
    </>
  );
  return (
    <div className={`comment-row${tone === 'default' ? '' : ` comment-row-${tone}`}${minimized ? ' is-minimized' : ''}`} id={anchor ?? undefined}>
      <Avatar login={author} url={avatar} size={40} />
      <article className="comment">
        <header className="comment-head">
          <span className="text-bold">{author}</span>
          <span>{verb}</span>
          <span>{url && anchor ? <a href={`#${anchor}`} className="comment-time">{<RelativeTime when={when} />}</a> : <RelativeTime when={when} />}</span>
          {editedAt && <span className="text-muted" title={`Edited ${new Date(editedAt).toLocaleString()}`}>· edited</span>}
          {badge}
          <RoleBadge association={association} />
          {(tools || url) && (
            <span className="comment-head-tools">
              {tools}
              {url && <a href={url} target="_blank" rel="noopener noreferrer" title="On GitHub"><Icon name="link" /></a>}
            </span>
          )}
        </header>
        {minimized ? <Minimized reason={minimizedReason}>{body}</Minimized> : body}
      </article>
    </div>
  );
}

/** The icon and colour of a rail badge per event kind. */
function badgeFor(item: TimelineItem): { icon: IconName; tone: string } {
  switch (item.kind) {
    case 'commit': return { icon: 'git-commit', tone: '' };
    case 'force-push': case 'base-force-push': return { icon: 'sync', tone: '' };
    case 'merged': return { icon: 'git-merge', tone: 'is-done' };
    case 'closed': return { icon: 'git-pull-request-closed', tone: 'is-danger' };
    case 'reopened': return { icon: 'git-pull-request', tone: 'is-success' };
    case 'ready': return { icon: 'git-pull-request', tone: '' };
    case 'draft': return { icon: 'git-pull-request-draft', tone: '' };
    case 'labeled': case 'unlabeled': return { icon: 'tag', tone: '' };
    case 'assigned': case 'unassigned': return { icon: 'person', tone: '' };
    case 'review-requested': case 'review-request-removed': return { icon: 'eye', tone: '' };
    case 'review-dismissed': return { icon: 'x', tone: '' };
    case 'renamed': return { icon: 'pencil', tone: '' };
    case 'base-changed': case 'head-deleted': case 'head-restored': return { icon: 'git-branch', tone: '' };
    case 'cross-referenced': case 'referenced': return { icon: 'link', tone: '' };
    case 'milestoned': case 'demilestoned': return { icon: 'tag', tone: '' };
    case 'locked': case 'unlocked': return { icon: 'lock', tone: '' };
    case 'auto-merge-on': case 'auto-merge-off': case 'queued': case 'dequeued': return { icon: 'git-merge', tone: '' };
    case 'deployed': return { icon: 'arrow-right', tone: '' };
    default: return { icon: 'dot-fill', tone: '' };
  }
}

const sha8 = (sha: string | null | undefined) => (sha ? sha.slice(0, 7) : '');

/** One sentence for an event, in GitHub's words. */
function sentence(item: TimelineItem, baseLabel: string): ReactNode {
  const t = item.target ? <span className="text-bold">{item.target}</span> : null;
  switch (item.kind) {
    case 'force-push': return <>force-pushed the branch from <code>{sha8(item.previous)}</code> to <code>{sha8(item.sha)}</code></>;
    case 'base-force-push': return <>force-pushed the <span className="branch-name">{baseLabel}</span> branch from <code>{sha8(item.previous)}</code> to <code>{sha8(item.sha)}</code></>;
    case 'merged': return <>merged commit <code>{sha8(item.sha)}</code> into <span className="branch-name">{item.target ?? baseLabel}</span></>;
    case 'closed': return <>closed this</>;
    case 'reopened': return <>reopened this</>;
    case 'ready': return <>marked this pull request as ready for review</>;
    case 'draft': return <>marked this pull request as draft</>;
    case 'labeled': return <>added the <IssueLabel name={item.target ?? ''} color={item.targetColor} /> label</>;
    case 'unlabeled': return <>removed the <IssueLabel name={item.target ?? ''} color={item.targetColor} /> label</>;
    case 'assigned': return <>assigned {t}</>;
    case 'unassigned': return <>unassigned {t}</>;
    case 'review-requested': return <>requested a review from {t}</>;
    case 'review-request-removed': return <>removed the request for review from {t}</>;
    case 'review-dismissed': return <>dismissed {t}’s review{item.message ? <>: <em>{item.message}</em></> : null}</>;
    case 'renamed': return <>changed the title <del>{item.previous}</del> {t}</>;
    case 'base-changed': return <>changed the base branch from <span className="branch-name">{item.previous}</span> to <span className="branch-name">{item.target}</span></>;
    case 'head-deleted': return <>deleted the <span className="branch-name">{item.target}</span> branch</>;
    case 'head-restored': return <>restored the head branch</>;
    case 'cross-referenced': {
      const r = item.reference;
      if (!r) {
        return <>mentioned this</>;
      }
      const name = <>{r.repository ? `${r.repository}#${r.number}` : `#${r.number}`} {r.title}</>;
      return <>{item.message === 'closes' ? 'will close this in ' : 'mentioned this in '}{r.url ? <a href={r.url} target="_blank" rel="noopener noreferrer">{name}</a> : name}</>;
    }
    case 'referenced': return <>referenced this in <code>{sha8(item.sha)}</code>{item.message ? <> {item.message}</> : null}{item.target ? <> ({item.target})</> : null}</>;
    case 'milestoned': return <>added this to the {t} milestone</>;
    case 'demilestoned': return <>removed this from the {t} milestone</>;
    case 'locked': return <>locked this conversation{item.message ? <> as <span className="text-bold">{item.message.toLowerCase().replace('_', ' ')}</span></> : null}</>;
    case 'unlocked': return <>unlocked this conversation</>;
    case 'auto-merge-on': return <>enabled auto-merge</>;
    case 'auto-merge-off': return <>disabled auto-merge{item.message ? <>: <em>{item.message}</em></> : null}</>;
    case 'queued': return <>added this pull request to the merge queue</>;
    case 'dequeued': return <>removed this pull request from the merge queue</>;
    case 'deployed': return <>deployed this{item.target ? <> to <span className="text-bold">{item.target}</span></> : null}</>;
    default: return <>{item.kind.replace(/-/g, ' ')}</>;
  }
}

/** An event row on the rail: the badge, the actor, the sentence, the time. */
export function EventRow({ item, baseLabel }: { item: TimelineItem; baseLabel: string }) {
  const { icon, tone } = badgeFor(item);
  return (
    <li className={`timeline-item timeline-${item.kind}`}>
      <span className={`timeline-badge ${tone}`}><Icon name={icon} /></span>
      <span className="timeline-meta">
        <span className="text-bold">{item.actor}</span>
        {/* One span, so the row's flex gap falls between actor, sentence and time, never inside the sentence. */}
        <span className="timeline-sentence">{sentence(item, baseLabel)}</span>
        <RelativeTime when={item.when} />
      </span>
    </li>
  );
}

/** A commit pushed to the branch: GitHub's compact row with the picture, the headline and the sha. */
export function CommitRow({ item, link }: { item: TimelineItem; link?: string }) {
  return (
    <li className="timeline-item timeline-commit">
      <span className="timeline-badge"><Icon name="git-commit" /></span>
      <span className="timeline-commit-line">
        <Avatar login={item.actor} url={item.avatar} size={16} />
        <span className="timeline-text">{item.text}</span>
        {item.verified === true && <span className="verified-badge" title="This commit was signed with a verified signature">Verified</span>}
        {link ? <Link to={link}><code>{sha8(item.sha)}</code></Link> : <code className="text-muted">{sha8(item.sha)}</code>}
      </span>
    </li>
  );
}

/** A review's header words and colour, from its state. */
export function reviewLook(state: string | null): { verb: string; icon: IconName; tone: 'default' | 'success' | 'danger' } {
  switch (state) {
    case 'APPROVED': return { verb: 'approved these changes', icon: 'check', tone: 'success' };
    case 'CHANGES_REQUESTED': return { verb: 'requested changes', icon: 'x', tone: 'danger' };
    case 'DISMISSED': return { verb: 'left a review that was dismissed', icon: 'x', tone: 'default' };
    default: return { verb: 'reviewed', icon: 'eye', tone: 'default' };
  }
}

interface ReviewCardProps {
  item: TimelineItem;
  /** The threads written in this review, listed under it as GitHub lists them. */
  threads: ReviewThread[];
  renderThread: (thread: ReviewThread) => ReactNode;
  onOpenPath: (path: string) => void;
  /** Where a thread's file opens in the review (Files changed, that file). */
  fileLink: (path: string) => string;
  /** Header tools: Edit, Dismiss review. */
  tools?: ReactNode;
  /** An editor in place of the summary while it is being edited. */
  editor?: ReactNode;
  onReact?: (content: string, on: boolean) => void;
}

/** A review on the rail: its state as the badge, its summary as a card, its threads under it. */
export function ReviewCard({ item, threads, renderThread, onOpenPath, fileLink, tools, editor, onReact }: ReviewCardProps) {
  const { verb, icon, tone } = reviewLook(item.state);
  const anchor = anchorOf(item.url);
  return (
    <li className={`timeline-item timeline-review timeline-review-${tone}`} id={anchor ?? undefined}>
      <span className={`timeline-badge ${tone === 'success' ? 'is-success' : tone === 'danger' ? 'is-danger' : ''}`}><Icon name={icon} /></span>
      <div className="timeline-review-body">
        <div className="timeline-meta">
          <Avatar login={item.actor} url={item.avatar} size={20} />
          <span className="text-bold">{item.actor}</span> {verb} <RelativeTime when={item.when} />
          {item.editedAt && <span className="text-muted" title={`Edited ${new Date(item.editedAt).toLocaleString()}`}>· edited</span>}
          <RoleBadge association={item.association} />
          {item.url && <a href={item.url} target="_blank" rel="noopener noreferrer" className="text-muted" title="On GitHub"><Icon name="link" /></a>}
          {tools && <span className="comment-head-tools">{tools}</span>}
        </div>
        {editor ? (
          <div className="comment comment-review-summary"><div className="comment-body">{editor}</div></div>
        ) : (item.html || onReact) && (
          <div className="comment comment-review-summary">
            {item.html && <div className="comment-body"><FragmentHost html={item.html} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} /></div>}
            <Reactions reactions={item.reactions} onToggle={onReact} />
          </div>
        )}
        {threads.length > 0 && (
          <div className="review-threads">
            {threads.map((t) => (
              <div key={t.id} className="review-thread">
                <div className="review-thread-file">
                  <Icon name="file" className="text-muted" />
                  <Link to={fileLink(t.path)} title={t.pathLabel}>{t.pathLabel}</Link>
                  {t.outdated && <span className="row-badge">Outdated</span>}
                  {t.resolved && <span className="row-badge">Resolved</span>}
                </div>
                {t.comments[0]?.diffHunk && <pre className="diff-hunk">{t.comments[0].diffHunk}</pre>}
                {renderThread(t)}
              </div>
            ))}
          </div>
        )}
      </div>
    </li>
  );
}
