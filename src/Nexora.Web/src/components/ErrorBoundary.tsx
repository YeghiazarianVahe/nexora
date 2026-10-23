import { Component, type ErrorInfo, type ReactNode } from 'react';

interface Props {
  children: ReactNode;
  /** What was being shown, for the message. */
  what?: string;
  /** Called with the error (the app logs it to the console; tests use it). */
  onError?: (error: Error) => void;
}

/**
 * A rendering error in one page or widget must not blank the whole application. The boundary shows what happened, keeps the rest of the
 * window (menu, connection indicator) alive and offers Try again. One around each route and one around the shell.
 */
export class ErrorBoundary extends Component<Props, { error: Error | null }> {
  state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Rendering failed', error, info.componentStack);
    this.props.onError?.(error);
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div className="notice error" role="alert" data-testid="error-boundary">
        <b>Something went wrong{this.props.what ? ` while showing ${this.props.what}` : ''}.</b>
        <p className="muted" style={{ margin: '6px 0' }}>{this.state.error.message}</p>
        <button onClick={() => this.setState({ error: null })}>Try again</button>{' '}
        <button className="ghost" onClick={() => window.location.reload()}>Reload the page</button>
      </div>
    );
  }
}
