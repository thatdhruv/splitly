// Thin wrapper over the browser's WebAuthn API. The server (ASP.NET Core Identity) produces and verifies the JSON;
// the browser only turns it into a credential and back, using the standard JSON helpers.
window.splitlyPasskey = {
    supported() {
        return !!(window.PublicKeyCredential
            && PublicKeyCredential.parseCreationOptionsFromJSON
            && PublicKeyCredential.parseRequestOptionsFromJSON);
    },

    async create(optionsJson) {
        const options = PublicKeyCredential.parseCreationOptionsFromJSON(JSON.parse(optionsJson));
        const credential = await navigator.credentials.create({ publicKey: options });
        return JSON.stringify(credential.toJSON());
    },

    async get(optionsJson) {
        const options = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson));
        const credential = await navigator.credentials.get({ publicKey: options });
        return JSON.stringify(credential.toJSON());
    },
};
