# ToolUp.Secrets.AwsSecretsManager

AWS Secrets Manager `ISecretStore` companion for `ToolUp.Platform`. Reads, writes, and lists secrets per call against a configured AWS region; supports scope-prefixed name conventions and AWS's default scheduled-deletion behaviour.

Credentials flow through the AWS SDK default chain (env vars, shared credentials file, EC2 instance profile, IAM role for ECS tasks, SSO). Secrets are never cached in process beyond the call boundary — rotation in Secrets Manager is picked up on next request.

## Minimum IAM policy

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": [
      "secretsmanager:GetSecretValue",
      "secretsmanager:CreateSecret",
      "secretsmanager:PutSecretValue",
      "secretsmanager:DeleteSecret"
    ],
    "Resource": "arn:aws:secretsmanager:<region>:<account>:secret:toolup/*"
  }, {
    "Effect": "Allow",
    "Action": "secretsmanager:ListSecrets",
    "Resource": "*"
  }]
}
```

`ListSecrets` is account-wide by AWS design (no resource-level filter); the companion filters client-side by name prefix.

## Activation

Set in the deployment's environment:

```
TOOLUP_SECRET_STORE=aws-secrets-manager
TOOLUP_AWS_SECRETS_REGION=eu-west-2
```

Licensed under Apache-2.0. Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
