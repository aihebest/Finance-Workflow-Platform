# Custom domain — `finance.desiconapp.com`

Replacing `fde-desicon-fw-dev-e5deetfbdxfvfsfq.z01.azurefd.net` with an address
that reads like Desicon.

This is not cosmetic. That generated hostname is about to appear in every
approval notification this platform sends — to Heads of Department, to the
Accounts Manager, to the DMD, most of them on a phone. An address nobody can
read is an address nobody trusts, and a link people hesitate over is a link
that does not get clicked.

## The address

**`finance.desiconapp.com`** — one address, no environment suffix.

Decided 22 August 2026. There is no separate production environment planned:
the environment currently named `dev` is the one Desicon will run on. Giving it
`finance-dev` now and migrating later would drag every bookmark, every
notification link already sent, and the Entra redirect URI along with it — so
the permanent address is used from the start.

`finance` rather than `fw`: the Azure resources are named `desicon-fw-*` and
should stay that way, but the address a Head of Department sees is not the
place to expose an internal abbreviation.

If a second environment is ever added, it takes the suffix — `finance-uat` —
and this one keeps the clean name it already has.

> **The directory is still called `dev`, and so are the resources.** That is
> now a hazard rather than an untidiness: guards that decide what is safe by
> looking for "dev" in a name are looking at the environment that holds real
> records. See docs/15 §7. One such guard existed, in
> `scripts/reset-dev-requests.sql`, and has been rewritten.

Set per environment in `<env>.auto.tfvars`:

```hcl
custom_domain_host_name = "finance.desiconapp.com"
```

Leave it unset and only the generated hostname is served, which is the shape
every environment had before this.

## This is additive

Both Front Door routes keep `link_to_default_domain = true`. The
`azurefd.net` hostname keeps answering after the custom domain goes live.

That matters right now: notifications already sent contain the old link, and
Monday's walkthrough with Chima and Tomy can proceed on either address. Nothing
that works today stops working.

---

## Where the DNS lives

`desiconapp.com` is **Managed at Microsoft 365**, not an Azure DNS zone.
Records are added at:

> Microsoft 365 admin center → Settings → Domains → `desiconapp.com` →
> DNS records → **Add record**

So `custom_domain_dns_zone_id` stays `null` in Terraform. There is no Azure
zone resource for this domain, and Terraform cannot publish the records itself.
It prints them instead.

**Front Door's validation record is `_dnsauth.<label>`.** The records already on
this domain use `asuid.<label>` — that is App Service and Static Web Apps
domain verification, a different mechanism for a different service. Copying the
`asuid` pattern here produces a domain that sits in *Pending* forever and never
says why.

---

## Order of operations

The steps are not interchangeable, and the wrong order produces a broken-looking
site rather than an error.

### 1. Apply

Every `./scripts/...` line below is written relative to the repository root.
Start each one by jumping there, so it does not matter which directory you are
standing in:

```powershell
Set-Location (git rev-parse --show-toplevel)
./scripts/sync-deployer-ip.ps1

cd infra/terraform/environments/dev
terraform plan     # read it
terraform apply    # then type: yes
```

**Do not skip the IP sync, and do not skip the plan.**

`deployer_ip_addresses` in `dev.auto.tfvars` is an allow-list containing the
address Terraform itself calls from. Dev has no private endpoints, so Key Vault,
SQL and Storage are reached over the public internet through that list. If the
value is stale — and it rotates on this ISP, and changes whenever the network
changes — Terraform rewrites the Key Vault ACL early in the graph without its
own address in it, and then locks itself out. The failure surfaces several steps
later as `403 ForbiddenByFirewall ... caller is not a trusted service`, which
reads like a permissions problem and is not one.

At the `Enter a value:` prompt, type `yes` — the word `yes`, not the command.

Avoid `-auto-approve` here. This apply changes both Front Door routes and the
WAF security policy; the plan is worth twenty seconds of reading.

This creates the custom domain in Front Door, attaches it to both routes, and
adds it to the WAF security policy. The domain will sit in **Pending**. It is
not live yet and the address will not resolve.

### Renaming an existing custom domain

Changing `custom_domain_host_name` after it has been applied is a *replacement*,
and Azure will refuse a naive one:

```
BadRequest: This resource is still associated with a route.
Please delete the association with the route first.
```

The module now carries `create_before_destroy = true` on the custom domain, so
the new one is created and the routes repointed before the old is removed. If
an apply has already failed halfway and left the old domain in place, the
deterministic recovery is two applies rather than fighting the ordering:

```powershell
# 1. detach and remove the old domain
#    set custom_domain_host_name = null in dev.auto.tfvars
terraform apply

# 2. create the new one
#    set custom_domain_host_name = "finance.desiconapp.com"
terraform apply
```

Safe to do whenever nothing has been published to DNS yet, because the domain
being destroyed was never serving traffic.

### 2. Read the records Terraform produced

```powershell
terraform output -json custom_domain_dns_records
```

### 3. Publish the TXT record, and only the TXT

In the Microsoft 365 admin center:

| Field | Value |
|---|---|
| Type | `TXT` |
| Name | `_dnsauth.finance` |
| Value | the `validation_token` from the output |
| TTL | 1 Hour |

### 4. Wait for Azure to approve it

Typically about fifteen minutes, sometimes longer. Check with:

```powershell
az afd custom-domain show `
  --resource-group rg-desicon-fw-dev `
  --profile-name afd-desicon-fw-dev `
  --custom-domain-name finance-desiconapp-com `
  --query "{domain:hostName, validation:domainValidationState, cert:tlsSettings.certificateType}" -o table
```

Wait for `domainValidationState` to read **Approved**. The managed certificate
issues automatically once it does — there is no certificate to buy, install, or
diarise for renewal.

### 5. Publish the CNAME — after approval, not before

| Field | Value |
|---|---|
| Type | `CNAME` |
| Name | `finance` |
| Value | `fde-desicon-fw-dev-e5deetfbdxfvfsfq.z01.azurefd.net` |
| TTL | 1 Hour |

**Last, deliberately.** A CNAME pointed at Front Door before the domain is
validated and routed will resolve and then serve an error. To anyone who tries
the address in that window it looks exactly like a broken deployment, and it is
the sort of thing that gets reported to management as "the new system is down".

### 6. Register the redirect URI in Entra — the step that breaks sign-in

`msal.ts` uses `redirectUri: window.location.origin`, so the SPA needs no code
change; it will ask Entra to return the user to whichever address they arrived
on. But Entra will refuse an address that is not registered.

The existing bootstrap script adds it, idempotently and without disturbing the
URI already there:

```powershell
Set-Location (git rev-parse --show-toplevel)

./scripts/bootstrap-spa-registration.ps1 `
  -ApiClientId 8deb5019-590d-4ef3-bb61-f5d450d341b5 `
  -RedirectUri "https://finance.desiconapp.com"
```

`Set-Location (git rev-parse --show-toplevel)` is not decoration. Four commands
in this runbook were first written as `./scripts/...` or `../../../scripts/...`
and handed over while the reader was standing in
`infra/terraform/environments/dev`, where none of them resolve. Anchoring to the
repository root removes the assumption rather than restating it.

It prints `Redirect URI already registered under the SPA platform` if it has
already been done, so it is safe to run twice.

**Both URIs must remain.** Removing the `azurefd.net` one to "tidy up" breaks
sign-in for everyone still using it, including anyone following a notification
sent before today.

This failure is worth recognising in advance because it does not look like a
platform problem. The user reaches the Microsoft sign-in page and is refused
*there*, with `AADSTS50011: The redirect URI specified in the request does not
match the redirect URIs configured for the application`. Nothing in this
platform logs anything, because the request never reached it.

### 7. Confirm

```powershell
curl.exe -I https://finance.desiconapp.com/healthz      # SPA origin
curl.exe -I https://finance.desiconapp.com/health/ready # API through /api routing
```

Then confirm the WAF actually covers the new domain — see the section below for
why this is not a formality:

```powershell
# Every domain on the profile...
az afd custom-domain list -g rg-desicon-fw-dev --profile-name afd-desicon-fw-dev `
  --query "[].{domain:hostName, id:id}" -o table

# ...must appear in the security policy's association.
az afd security-policy show -g rg-desicon-fw-dev --profile-name afd-desicon-fw-dev `
  --security-policy-name secpol-desicon-fw-dev `
  --query "parameters.associations[].domains[].id" -o tsv
```

The second list must contain the custom domain's id. If it does not, the site
is serving unprotected on that address while every dashboard still reads green.

Then sign in on the new address and open one request.

---

## The WAF association

`modules/frontdoor/main.tf` adds the custom domain to the security policy
alongside the endpoint. This is the part most likely to be got wrong when
someone repeats this for uat or prd by hand.

A Front Door WAF policy protects **the domains it is associated with**, not the
profile. Attach a custom domain to the routes and leave the security policy
naming only the endpoint, and the result is a site that works perfectly on the
new address with no firewall in front of it — while the portal still shows a
Premium profile, a Prevention-mode policy, and managed rule sets enabled.

Everything reads green. Nothing fails. That is precisely the class of defect
this project has found over and over, and it would be self-inflicted here: the
same WAF that blocked every receipt upload in August would quietly stop
inspecting anything on the address everyone had moved to.

The Terraform does it with a `dynamic "domain"` block, so it cannot be
forgotten when `custom_domain_host_name` is set for the next environment.

---

## Still to decide

- [ ] Whether uat and prd move at the same time, or prd waits until after
      go-live. Doing prd last means the address in the go-live announcement is
      the permanent one, which argues for doing it *before* rather than after.
- [ ] Whether the `azurefd.net` hostname is eventually retired. It costs
      nothing to keep and is a useful fallback if DNS is ever misconfigured, but
      leaving it reachable means the platform answers on an address no policy
      or documentation mentions.
