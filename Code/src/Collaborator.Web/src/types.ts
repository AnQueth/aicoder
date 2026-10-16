export interface Product {
  id: string
  name: string
  brief: string
  repositoryUrl: string | null
  repositoryBaseBranch: string | null
}

export interface Developer {
  userId: string
  displayName: string
  reviewFocus: string | null
  rolesJson: string
  isEnabled: boolean
}

export interface SkillIdentity {
  roleId: string
  domain: string
  skillId: string
}

export interface Skill extends SkillIdentity {
  name: string
  prompt: string
  activeRevision: number
  isEnabled: boolean
}

export interface SkillRevision {
  revision: number
  name: string
  prompt: string
  createdAt: string
  createdBy: string | null
  changeReason: string | null
}

export interface FeatureRequest {
  text: string
  businessAnalystId: string
  businessAnalystSkillId: string
  reviewerCount: number
  developerSkills?: SkillIdentity[]
  planningMode?: 'proposals-first' | 'draft-first'
}

export interface RunSummary {
  id: string
  productId: string
  text: string
  status: string
  createdAt: string
}

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue }

export interface Specification {
  title: string
  problemStatement: string
  userStories: string[]
  acceptanceCriteria: string[]
  constraints: string[]
  outOfScope: string[]
  openQuestions: string[]
}

export interface Run extends RunSummary {
  planningMode?: 'proposals-first' | 'draft-first' | null
  businessAnalystId: string
  featureSpecificationRevision: number
  architectureRevision: number
  planRevision: number
  reviewerCount: number
  failureCode: string | null
  repositoryUrl: string | null
  repositoryBaseBranch: string | null
  repositoryCommit: string | null
  featureSpecification: Specification | null
  plan: { summary: string; steps: string[]; openQuestions: string[] } | null
  codeChange: {
    status: string
    summary: string | null
    branchName: string | null
    commit: string | null
    changedFiles: string[]
    failureCode: string | null
  } | null
}

export interface DeveloperProposal {
  userId: string
  displayName: string
  matchedRoles: string[]
  reviewFocus: string | null
  proposal: string | null
  noAdditionalInput: boolean
  submittedAt: string | null
}

export interface DeveloperProposalRound {
  revision: number
  status: 'collecting' | 'ready'
  developers: DeveloperProposal[]
}

export interface Assignment {
  revision: number
  userId: string
  developerDisplayName: string
  reviewFocus: string | null
  matchedRoles: string[]
  reviewPrompt: string
  decision: string | null
  response: string | null
  status: string
}

export interface RunEvent {
  id: number
  kind: string
  actor: string
  createdAt: string
}

export interface Page<T> {
  items: T[]
  total: number
  offset: number
  limit: number
}

export function skillKey(skill: SkillIdentity): string {
  return `${skill.roleId}/${skill.domain}/${skill.skillId}`
}

export function words(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]/g, ' ')
}

export function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
