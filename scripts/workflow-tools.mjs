// Workflow schemas mirrored from the native /mcp tools/list contract.
// Both transports execute WorkspaceMcpTools; regenerate after changing its input DTO.
export const workflowTools = [
  {
    "name": "get_task_workflow",
    "description": "Get a real board task's parent request, plan, repository/file references, comments, results and human board links. For older unlinked tasks, use get_task and get_project_context.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "taskId": {
          "type": "integer"
        }
      },
      "required": [
        "taskId"
      ]
    }
  },
  {
    "name": "workflow_command",
    "description": "Write to the same workflow the human manages. input.operation: capture (capture object), adopt_idea (ideaId), create_project (name), register_repository (projectId,name,path), attach_project (projectId), save_brief (brief), save_plan (tasks; for published work also taskBaselines copied from taskId/editToken in the original context read), publish_plan (planId), add_message (kind,text), set_task_state (itemId,status), report_result (itemId,evidence), accept_result (itemId,evidenceId,checkedCriterionIds), conclude (text,limitations), archive (archived). Always provide actor and a unique key. Except the first four operations, also provide requestId and expectedVersion from get_request_context. Retry the identical payload with the same key after a lost response. Refresh on conflict. Task drafts need stable GUID ids; dependencies reference draft ids. For MCP use schema enum names such as Todo and Medium; the REST command uses existing numeric enum values. Reports are caller observations, not proof of execution. No Codex process is launched.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "input": {
          "type": "object",
          "properties": {
            "operation": {
              "type": "string"
            },
            "actor": {
              "type": "string"
            },
            "key": {
              "type": "string"
            },
            "requestId": {
              "type": [
                "string",
                "null"
              ],
              "format": "uuid"
            },
            "expectedVersion": {
              "type": [
                "integer",
                "null"
              ]
            },
            "ideaId": {
              "type": [
                "integer",
                "null"
              ]
            },
            "projectId": {
              "type": [
                "integer",
                "null"
              ]
            },
            "capture": {
              "type": [
                "object",
                "null"
              ],
              "properties": {
                "title": {
                  "type": "string"
                },
                "original": {
                  "type": "string"
                },
                "projectId": {
                  "type": [
                    "integer",
                    "null"
                  ]
                },
                "source": {
                  "type": "string",
                  "default": "board"
                }
              },
              "required": [
                "title",
                "original",
                "projectId"
              ]
            },
            "brief": {
              "type": [
                "object",
                "null"
              ],
              "properties": {
                "outcome": {
                  "type": "string"
                },
                "scope": {
                  "type": "string"
                },
                "constraints": {
                  "type": "string"
                },
                "acceptance": {
                  "type": "string"
                }
              },
              "required": [
                "outcome",
                "scope",
                "constraints",
                "acceptance"
              ]
            },
            "tasks": {
              "type": [
                "array",
                "null"
              ],
              "items": {
                "type": "object",
                "properties": {
                  "id": {
                    "type": "string",
                    "format": "uuid"
                  },
                  "title": {
                    "type": "string"
                  },
                  "description": {
                    "type": "string"
                  },
                  "acceptance": {
                    "type": "string"
                  },
                  "repositoryId": {
                    "type": [
                      "integer",
                      "null"
                    ]
                  },
                  "fileReferences": {
                    "type": "string"
                  },
                  "priority": {
                    "type": "string",
                    "enum": [
                      "Low",
                      "Medium",
                      "High",
                      "Critical"
                    ]
                  },
                  "assignee": {
                    "type": "string"
                  },
                  "dependencies": {
                    "type": "array",
                    "items": {
                      "type": "string",
                      "format": "uuid"
                    }
                  }
                },
                "required": [
                  "id",
                  "title",
                  "description",
                  "acceptance",
                  "repositoryId",
                  "fileReferences",
                  "priority",
                  "assignee",
                  "dependencies"
                ]
              }
            },
            "taskBaselines": {
              "type": [
                "object",
                "null"
              ],
              "additionalProperties": {
                "type": "string"
              }
            },
            "planId": {
              "type": [
                "string",
                "null"
              ],
              "format": "uuid"
            },
            "itemId": {
              "type": [
                "string",
                "null"
              ],
              "format": "uuid"
            },
            "status": {
              "type": [
                "string",
                "null"
              ],
              "enum": [
                "Backlog",
                "Todo",
                "InProgress",
                "Review",
                "Done",
                null
              ]
            },
            "evidence": {
              "type": [
                "object",
                "null"
              ],
              "properties": {
                "summary": {
                  "type": "string"
                },
                "checks": {
                  "type": "string"
                },
                "codeReference": {
                  "type": "string"
                }
              },
              "required": [
                "summary",
                "checks",
                "codeReference"
              ]
            },
            "evidenceId": {
              "type": [
                "string",
                "null"
              ],
              "format": "uuid"
            },
            "checkedCriterionIds": {
              "type": [
                "array",
                "null"
              ],
              "items": {
                "type": "integer"
              }
            },
            "kind": {
              "type": [
                "string",
                "null"
              ]
            },
            "text": {
              "type": [
                "string",
                "null"
              ]
            },
            "limitations": {
              "type": [
                "string",
                "null"
              ]
            },
            "archived": {
              "type": [
                "boolean",
                "null"
              ]
            },
            "name": {
              "type": [
                "string",
                "null"
              ]
            },
            "path": {
              "type": [
                "string",
                "null"
              ]
            }
          }
        }
      },
      "required": [
        "input"
      ]
    }
  },
  {
    "name": "list_requests",
    "description": "List request workflows in the existing board; optionally filter by projectId. Use get_request_context before making a versioned change.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": [
            "integer",
            "null"
          ],
          "default": null
        }
      }
    }
  },
  {
    "name": "get_request_context",
    "description": "Get the preserved original ask, improved brief and plan revisions, existing idea/project/task IDs, repositories/files, task statuses/comments, reported evidence and conclusions. Current=false marks historical evidence. Links open the same human board.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "requestId": {
          "type": "string",
          "format": "uuid"
        }
      },
      "required": [
        "requestId"
      ]
    }
  }
];
