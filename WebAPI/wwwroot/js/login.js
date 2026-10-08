document.addEventListener('DOMContentLoaded', () => {
    const loginForm = document.getElementById('loginForm');
    const usernameInput = document.getElementById('username');
    const passwordInput = document.getElementById('password');
    const errorMessageDiv = document.getElementById('errorMessage');
    const loginButton = loginForm ? loginForm.querySelector('button[type="submit"]') : null;

    if (loginForm && loginButton) {
        loginForm.addEventListener('submit', async (event) => {
            event.preventDefault();
            clearError();
            setLoading(true);

            const username = usernameInput.value.trim();
            const password = passwordInput.value.trim();

            if (!username || !password) {
                showError('Username and Password are required.');
                setLoading(false);
                return;
            }

            try {
                const response = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                    },
                    body: JSON.stringify({ username, password }),
                });

                if (response.ok) {
                    // Successful login
                    if (typeof toastr !== 'undefined') {
                        toastr.success('Login successful! Redirecting...');
                    }
                    window.location.href = '/panel.html'; // Redirect to the control panel
                } else {
                    const errorData = await response.json().catch(() => null);
                    const message = errorData?.message || `Login failed. Status: ${response.status}`;
                    showError(message);
                    if (typeof toastr !== 'undefined') {
                        toastr.error(message);
                    }
                }
            } catch (error) {
                console.error('Login request error:', error);
                const message = 'An error occurred during login. Please try again.';
                showError(message);
                if (typeof toastr !== 'undefined') {
                    toastr.error(message);
                }
            } finally {
                setLoading(false);
            }
        });
    } else {
        if (!loginForm) console.error('Login form not found');
        if (!loginButton) console.error('Login button not found');
    }

    function showError(message) {
        if (errorMessageDiv) {
            errorMessageDiv.textContent = message;
            errorMessageDiv.style.display = 'block';
        }
    }

    function clearError() {
        if (errorMessageDiv) {
            errorMessageDiv.textContent = '';
            errorMessageDiv.style.display = 'none';
        }
    }

    function setLoading(isLoading) {
        if (loginButton) {
            if (isLoading) {
                loginButton.disabled = true;
                loginButton.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Logging In...';
            } else {
                loginButton.disabled = false;
                loginButton.innerHTML = 'Login';
            }
        }
    }
});
