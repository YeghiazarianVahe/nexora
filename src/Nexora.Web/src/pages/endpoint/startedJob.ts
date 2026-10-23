import { createContext, useContext } from 'react';

/**
 * A job started from any tab opens its output window in place (the tab keeps its state); "Open job" in that window goes to the job's page.
 * The workstation page owns the window and provides this to its tabs.
 */
export const StartedJobContext = createContext<(jobId: string) => void>(() => undefined);
export const useOnJobStarted = () => useContext(StartedJobContext);
