import { createRoot } from 'react-dom/client';
import './styles.css';
import { App } from './App';

// No <StrictMode>: its double-invoked effects would open two session sockets,
// and the single-use token can only authenticate one of them.
createRoot(document.getElementById('root')!).render(<App />);
