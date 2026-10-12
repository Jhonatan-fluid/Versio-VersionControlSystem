import { Link, NavLink } from 'react-router';
import type { Branch, Me } from '../api';
import { api } from '../api';
import { historyUrl, repoUrl, treeUrl } from '../routes';

interface Props {
  me: Me | null;
  repo?: { owner: string; name: string; ref: string; branches: Branch[]; branchesTruncated?: boolean; onRef: (ref: string) => void; compareHref: string };
  onToggleTree?: () => void;
}

export function Header({ me, repo, onToggleTree }: Props) {
  const here = window.location.pathname + window.location.search;
  return (
    <header className="topbar">
      {onToggleTree && (
        <button type="button" className="icon-button tree-toggle" onClick={onToggleTree} aria-label="Show or hide the tree">
          <span aria-hidden="true">{'☰'}</span>
        </button>
      )}
      <Link to="/" className="brand">Versio</Link>
      {repo && (
        <>
          <span className="crumb">
            <Link to={repoUrl(repo.owner, repo.name)}>{repo.owner}/{repo.name}</Link>
          </span>
          <label className="branch">
            <span className="visually-hidden">Branch</span>
            <select value={repo.ref} onChange={(e) => repo.onRef(e.target.value)}>
              {!repo.branches.some((b) => b.name === repo.ref) && <option value={repo.ref}>{repo.ref.length === 40 ? repo.ref.slice(0, 8) : repo.ref}</option>}
              {repo.branches.map((b) => (
                <option key={b.name} value={b.name}>{b.name}</option>
              ))}
              {repo.branchesTruncated && (
                <option disabled value="">
                  {`Only the first ${repo.branches.length} branches are listed`}
                </option>
              )}
            </select>
          </label>
          <nav className="topnav" aria-label="Repository">
            <NavLink to={treeUrl(repo.owner, repo.name, repo.ref)} end={false}>Code</NavLink>
            <NavLink to={historyUrl(repo.owner, repo.name, repo.ref)}>History</NavLink>
            <NavLink to={repo.compareHref}>Compare</NavLink>
          </nav>
        </>
      )}
      <div className="spacer" />
      {me?.signedIn ? (
        <div className="user">
          {me.avatarUrl && <img className="avatar" src={me.avatarUrl} alt="" width={24} height={24} />}
          <span className="user-name">{me.login}</span>
          <button
            type="button"
            className="link-button"
            onClick={async () => {
              await api.logout();
              window.location.assign('/');
            }}
          >
            Sign out
          </button>
        </div>
      ) : me?.signInAvailable ? (
        <a className="button" href={api.signInUrl(here)}>Sign in with GitHub</a>
      ) : null}
    </header>
  );
}
