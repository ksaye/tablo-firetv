namespace TabloFireTv;

/// <summary>The sign-in page, shown until a Tablo account has been given.</summary>
public static class LoginPages
{
    public static string LoginPage(bool firstRun) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="dark">
        <title>Sign in · Tablo</title>
        <link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'><rect x='2' y='8' width='28' height='19' rx='3' fill='%234da3ff'/><path d='M13 14l7 4.5-7 4.5z' fill='%230d1117'/><path d='M9 8l7-5 7 5' stroke='%234da3ff' stroke-width='2.5' fill='none' stroke-linecap='round'/></svg>">
        <style>
          *{box-sizing:border-box}
          body{background:#0d1117;color:#e6edf3;font:16px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;
               display:grid;place-items:center;min-height:100vh;margin:0;padding:1.5rem}
          .signin{width:100%;max-width:24rem}
          .brand{display:flex;align-items:center;gap:.6rem;font-size:1.25rem;font-weight:600;margin-bottom:.25rem}
          .brand svg{width:1.9rem;height:1.9rem;fill:#4da3ff}
          .brand .tri{fill:#0d1117}
          .brand .ant{stroke:#4da3ff;stroke-width:2.5;fill:none;stroke-linecap:round}
          p.lede{color:#8b949e;margin:.25rem 0 1.5rem}
          label{display:block;font-size:.8rem;color:#8b949e;margin:.9rem 0 .3rem;letter-spacing:.02em}
          input[type=email],input[type=password],select{width:100%;padding:.65rem .75rem;border-radius:.5rem;
               border:1px solid #30363d;background:#161b22;color:#e6edf3;font-size:1rem}
          input:focus,select:focus{outline:2px solid #4da3ff;outline-offset:-1px}
          .row{display:flex;align-items:center;gap:.5rem;margin-top:1rem;color:#8b949e;font-size:.9rem}
          button{width:100%;margin-top:1.4rem;padding:.7rem;border:0;border-radius:.5rem;background:#4da3ff;
                 color:#0d1117;font-size:1rem;font-weight:600;cursor:pointer}
          button[disabled]{opacity:.6;cursor:progress}
          .error{margin-top:1rem;padding:.7rem .8rem;border-radius:.5rem;background:#3d1d1d;
                 border:1px solid #6e2b2b;color:#ffb4b4;font-size:.9rem}
          .note{margin-top:1.75rem;color:#6e7681;font-size:.8rem;line-height:1.5}
          [hidden]{display:none!important}
        </style>
        </head>
        <body>
        <form class="signin" id="form" autocomplete="on">
          <div class="brand">
            <svg viewBox="0 0 32 32" aria-hidden="true"><rect x="2" y="8" width="28" height="19" rx="3"/><path d="M13 14l7 4.5-7 4.5z" class="tri"/><path d="M9 8l7-5 7 5" class="ant"/></svg>
            <span>Tablo</span>
          </div>
          <p class="lede">{{(firstRun
              ? "Sign in with your Tablo account to connect this Fire TV to your DVR."
              : "Sign in with your Tablo account.")}}</p>

          <label for="email">Email</label>
          <input id="email" name="email" type="email" autocomplete="username" required autofocus>

          <label for="password">Password</label>
          <input id="password" name="password" type="password" autocomplete="current-password" required>

          <div id="devicePick" hidden>
            <label for="device">Which Tablo?</label>
            <select id="device"></select>
          </div>

          <label class="row"><input type="checkbox" id="remember" checked> Remember me on this Fire TV</label>

          <button type="submit" id="submit">Sign in</button>
          <div class="error" id="error" hidden></div>

          <p class="note">These are the credentials for your Tablo account — the same ones the
          Tablo app uses. They are sent to Tablo's own login service, and nowhere else.</p>
        </form>

        <script>
        const form = document.getElementById('form');
        const error = document.getElementById('error');
        const submit = document.getElementById('submit');

        form.addEventListener('submit', async (event) => {
          event.preventDefault();
          submit.disabled = true;
          submit.textContent = 'Signing in…';
          error.hidden = true;

          const pick = document.getElementById('devicePick');
          const body = {
            email: document.getElementById('email').value,
            password: document.getElementById('password').value,
            remember: document.getElementById('remember').checked,
            serverId: pick.hidden ? null : document.getElementById('device').value
          };

          try {
            const res = await fetch('/api/login', {
              method: 'POST',
              headers: { 'content-type': 'application/json' },
              body: JSON.stringify(body)
            });
            const data = await res.json();

            if (!res.ok) throw new Error(data.error || res.statusText);

            // The account has more than one DVR on it — ask which, then post again.
            if (data.chooseDevice) {
              const select = document.getElementById('device');
              select.replaceChildren(...data.devices.map((d) => {
                const option = document.createElement('option');
                option.value = d.serverId;
                option.textContent = d.name + ' · ' + d.host;
                return option;
              }));
              pick.hidden = false;
              submit.textContent = 'Connect';
              submit.disabled = false;
              return;
            }

            const next = new URLSearchParams(location.search).get('next');
            location.href = next && next.startsWith('/') && !next.startsWith('//') ? next : '/';
          } catch (err) {
            error.textContent = err.message;
            error.hidden = false;
            submit.disabled = false;
            submit.textContent = 'Sign in';
          }
        });
        </script>
        <script src="/nav.js"></script>
        </body>
        </html>
        """;
}
