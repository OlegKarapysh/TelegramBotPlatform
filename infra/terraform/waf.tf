# Restrict /admin/* to allowlisted IPs (in addition to the admin key). The webhook and health
# paths stay public. WAF is used because it is path-aware (L7); security groups (L4) cannot.
resource "aws_wafv2_ip_set" "admin_allowlist" {
  name               = "${var.project_name}-admin-allowlist"
  scope              = "REGIONAL"
  ip_address_version = "IPV4"
  addresses          = var.admin_allowlist_cidrs
}

resource "aws_wafv2_web_acl" "app" {
  name        = "${var.project_name}-web-acl"
  scope       = "REGIONAL"
  description = "Public webhook and health, admin path restricted to allowlisted IPs"

  default_action {
    allow {}
  }

  rule {
    name     = "block-admin-from-non-allowlisted"
    priority = 1

    action {
      block {}
    }

    statement {
      and_statement {
        statement {
          byte_match_statement {
            search_string         = "/admin"
            positional_constraint = "STARTS_WITH"
            field_to_match {
              uri_path {}
            }
            text_transformation {
              priority = 0
              type     = "NONE"
            }
          }
        }
        statement {
          not_statement {
            statement {
              ip_set_reference_statement {
                arn = aws_wafv2_ip_set.admin_allowlist.arn
              }
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${var.project_name}-block-admin"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "${var.project_name}-web-acl"
    sampled_requests_enabled   = true
  }
}

# The Terraform provider does not expose the Express-managed ALB ARN (ingress_paths only has
# access_type + endpoint). So associate the WebACL in a SECOND apply: the first apply creates the
# service and the WebACL; then find the ALB ARN and set var.alb_arn, and re-apply to associate.
variable "alb_arn" {
  type        = string
  default     = ""
  description = "ARN of the Express-managed ALB for the WAF association. Empty on the first apply; set it afterward."
}

resource "aws_wafv2_web_acl_association" "app" {
  count        = var.alb_arn == "" ? 0 : 1
  resource_arn = var.alb_arn
  web_acl_arn  = aws_wafv2_web_acl.app.arn
}
