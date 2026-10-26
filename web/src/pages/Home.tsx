import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { AppHeader } from '../components/AppHeader';
import { Button, Flash, Icon, LinkButton, Loading } from '../components/ui';
import { parseRepoInput, repoUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';

export function Home({ me }: { me: Me | null }) {
  const navigate = useNavigate();
  const [input, setInput] = useState('');
  const [error, setError] = useState<string | null>(null);
  const failed = new URLSearchParams(window.location.search).get('signin') === 'failed';
  const list = useAsync((s) => api.repos(s), []);
  usePageTitle();

  // A sign-in-only server (Web:AnonymousAccess off) shows nothing until the person signs in,
  // so the home page is the gate: the one thing on it is the way in.
  if (me && !me.signedIn && !me.anonymousAccess) {
    return (
      <div className="app">
        <AppHeader me={me} />
        <main className="home">
          <h1>Sign in</h1>
          <p className="text-muted">This server shows its repositories to signed-in people only.</p>
          {failed && <Flash scheme="danger">Sign-in did not complete. Try again.</Flash>}
          {me.signInAvailable ? (
            <p><LinkButton href={api.signInUrl('/')} variant="primary" leadingIcon="mark-github">Sign in with GitHub</LinkButton></p>
          ) : (
            <p className="text-danger">Sign-in is not configured on this server. Tell whoever runs it.</p>
          )}
        </main>
      </div>
    );
  }

  const scoped = list.state === 'ok' && list.value.scoped;
  return (
    <div className="app">
      <AppHeader me={me} />
      <main className="home">
        {failed && <Flash scheme="danger">Sign-in did not complete. Try again.</Flash>}
        {scoped ? (
          // The admin's list, already filtered by the server to what this caller can read: a
          // repository missing here is missing on purpose, so there is no open-by-name form
          // beside it — opening one off the list is "not found" anyway.
          <>
            <h1>Repositories</h1>
            <p className="text-muted">
              See a PLC project the way Versio shows it: data types and interfaces as tables, ladder and FBD as networks, Structured
              Text numbered as the IDE numbers it; review its pull requests with the code drawn.
            </p>
            {list.value.repos.length === 0 ? (
              <p className="text-muted">
                None of this server's repositories are visible to you{me && !me.signedIn && me.signInAvailable ? (
                  <>
                    {' — '}
                    <a href={api.signInUrl('/')}>sign in</a> to see yours
                  </>
                ) : null}.
              </p>
            ) : (
              <ul className="repo-list">
                {list.value.repos.map((r) => (
                  <li key={`${r.owner}/${r.name}`}>
                    <Link className="repo-row" to={repoUrl(r.owner, r.name)}>
                      <Icon name={r.private ? 'lock' : 'repo'} className="text-muted" />
                      <span className="repo-name">{r.owner}/{r.name}</span>
                      {r.private && <span className="repo-private">Private</span>}
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </>
        ) : (
          <>
            <h1>Open a repository</h1>
            <p className="text-muted">
              See a PLC project the way Versio shows it: data types and interfaces as tables, ladder and FBD as networks, Structured
              Text numbered as the IDE numbers it; review its pull requests with the code drawn.
            </p>
            {list.state === 'loading' ? (
              <Loading />
            ) : (
              <form
                className="open-form"
                onSubmit={(e) => {
                  e.preventDefault();
                  const parsed = parseRepoInput(input);
                  if (!parsed) {
                    setError('Enter owner/repository, or a github.com link to one.');
                    return;
                  }
                  navigate(repoUrl(parsed.owner, parsed.repo));
                }}
              >
                <label htmlFor="repo">Repository</label>
                <div className="open-row">
                  <input id="repo" className="form-control" value={input} onChange={(e) => setInput(e.target.value)} placeholder="owner/repository" autoComplete="off" spellCheck={false} />
                  <Button type="submit" variant="primary">Open</Button>
                </div>
                {error && <p className="text-danger">{error}</p>}
              </form>
            )}
          </>
        )}
        {me && !me.signedIn && (
          <p className="text-muted">
            {me.signInAvailable ? 'Public repositories open without signing in. Sign in to open private ones.' : 'Public repositories only: sign-in is not configured on this server.'}
          </p>
        )}
      </main>
    </div>
  );
}
