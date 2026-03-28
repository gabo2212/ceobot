(() => {
    async function loadHeader() {
        const mount = document.getElementById('site-header');
        if (!mount) return;

        const res = await fetch('/partials/header.html', { cache: 'no-store' });
        mount.innerHTML = await res.text();

        // highlight current page (optional)
        const here = location.pathname
            .replace(/\/index\.html$/, '/')
            .replace(/\/$/, '');
        mount.querySelectorAll('.site-nav a').forEach(a => {
            const p = a.getAttribute('href')
                ?.replace(/\/index\.html$/, '/')
                .replace(/\/$/, '');
            if (p === here) a.classList.add('active');
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', loadHeader);
    } else {
        loadHeader();
    }
})();
