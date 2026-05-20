window.banyan = {
    login: async (username, password) => {
        try {
            const resp = await fetch('/api/auth/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ username, password }),
                credentials: 'same-origin',
            });
            const data = await resp.json().catch(() => ({}));
            return {
                ok: resp.ok,
                message: data.message || data.error || (resp.ok ? '' : 'Sign-in failed'),
            };
        } catch (e) {
            return { ok: false, message: String(e) };
        }
    },

    logout: async () => {
        try {
            await fetch('/api/auth/logout', {
                method: 'POST',
                credentials: 'same-origin',
            });
        } catch {}
    },
};
