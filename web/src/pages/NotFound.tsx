import type { Me } from '../api';
import { AppHeader } from '../components/AppHeader';
import { Blankslate, LinkButton } from '../components/ui';
import { usePageTitle } from '../title';

/** A URL this page has no route for. It used to land on Home with nothing said. */
export function NotFound({ me }: { me: Me | null }) {
  usePageTitle('Page not found');
  return (
    <div className="app">
      <AppHeader me={me} />
      <main className="page">
        <Blankslate icon="alert" heading="This is not a page" spacious action={<LinkButton to="/">Go home</LinkButton>}>
          Nothing is at {window.location.pathname}. A link copied from github.com should work with the host swapped: a repository, a pull
          request, a commit, a branch or a release.
        </Blankslate>
      </main>
    </div>
  );
}
