import { Link } from 'react-router';

export function NotFound({ what = 'page' }: { what?: string }) {
  return (
    <article className="view">
      <h1>Not found</h1>
      <p>
        There is no {what} in this workspace. <Link to="/">Back to the overview</Link>.
      </p>
    </article>
  );
}
