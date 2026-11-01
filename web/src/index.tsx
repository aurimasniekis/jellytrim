import { render } from 'preact';
import { App } from './app';
import styles from './styles.css';

function isLightBackground(element: Element): boolean {
    // Jellyfin themes are not exposed as an API, so match whichever theme is rendered.
    const color = getComputedStyle(element).backgroundColor.match(/[\d.]+/g)?.map(Number) ?? [];
    if (color.length < 3 || color[3] === 0) return false;
    return (0.2126 * color[0] + 0.7152 * color[1] + 0.0722 * color[2]) / 255 > 0.6;
}

/** Controller contract of Jellyfin Web's viewContainer: instantiated with the page element. Must stay a plain function. */
export default function JellytrimController(this: unknown, view: HTMLElement) {
    const root = view.querySelector('#jellytrim-root') as HTMLElement;
    if (!document.getElementById('jellytrim-styles')) {
        const style = document.createElement('style');
        style.id = 'jellytrim-styles';
        style.textContent = styles;
        document.head.appendChild(style);
    }

    root.classList.add('jt');
    root.dataset.theme = isLightBackground(document.body) ? 'light' : 'dark';
    render(<App />, root);
    view.addEventListener('viewdestroy', () => render(null, root), { once: true });
}
